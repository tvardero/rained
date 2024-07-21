namespace RainEd;

using ImGuiNET;
using Raylib_cs;
using RectpackSharp;

class AssetGraphicsProvider
{
    private readonly Dictionary<string, RlManaged.Texture2D?> tileTexCache = [];
    private readonly Dictionary<string, RlManaged.Texture2D?> propTexCache = [];
    private readonly Dictionary<string, RlManaged.Image> _loadedTilePreviews = [];

    // tile previews are separate images...
    //private readonly Dictionary<string, RlManaged.Texture2D?> previewTexCache = [];

    private const int AtlasTextureWidth = 2048;
    private const int AtlasTextureHeight = 2048;
    private const int MaxRectsPerAtlas = 2048;

    private class TilePreviewAtlas
    {
        public RlManaged.Texture2D texture;
        public PackingRectangle[] rectangles;
        public List<string> tiles = [];
        public int[] idIndices;
        public int rectangleCount;

        public uint curX = 0;
        public uint curY = 0;
        public uint rowHeight = 0;

        public uint packWidth = 0;
        public uint packHeight = 0;

        public bool dirty = false;

        public TilePreviewAtlas()
        {
            using var img = RlManaged.Image.GenColor(AtlasTextureWidth, AtlasTextureHeight, Color.Blank);
            texture = RlManaged.Texture2D.LoadFromImage(img);
            rectangleCount = 0;
            rectangles = new PackingRectangle[MaxRectsPerAtlas];
            idIndices = new int[MaxRectsPerAtlas];
        }
    }

    private readonly List<TilePreviewAtlas> _tilePreviewAtlases = [];
    private readonly Dictionary<string, (int atlasIndex, int rectId)> _tilePreviewRects = [];

    // Does Path.Combine(directory, query)
    // On Linux, it does extra processing to account for the fact that
    // it uses a case-sensitive filesystem.
    public static string GetFilePath(string directory, string fileName)
    {
        var combined = Path.Combine(directory, fileName);

        if ((OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) && !File.Exists(combined))
        {
            foreach (var filePath in Directory.GetFiles(directory))
            {
                if (string.Equals(fileName, Path.GetFileName(filePath), StringComparison.InvariantCultureIgnoreCase))
                {
                    combined = filePath;
                    break;
                }
            }
        }

        return combined;
    }

    /// <summary>
    /// Obtain the texture of a prop asset. May be cached.
    /// </summary>
    /// <param name="assetName">The name of the asset.</param>
    /// <returns>The prop texture, or null if the graphics file was invalid or not found.</returns>
    public RlManaged.Texture2D? GetPropTexture(string assetName)
    {
        if (propTexCache.TryGetValue(assetName, out RlManaged.Texture2D? texture))
            return texture;

        // find prop path
        // for some reason, previews for drought props are in cast data instead of in the Props folder
        // kind of annoying. so i just put those images in assets/internal
        string texturePath = GetFilePath(Path.Combine(RainEd.Instance.AssetDataPath, "Props"), assetName + ".png");
        if (!File.Exists(texturePath) && DrizzleCastMap.TryGetValue(assetName, out string? castPath))
        {
            texturePath = Path.Combine(Boot.AppDataPath, "assets", "internal", castPath!);
        }

        texture = null;

        using var srcImage = RlManaged.Image.Load(texturePath);
        if (Raylib.IsImageReady(srcImage))
        {
            CropImage(srcImage);
            texture = RlManaged.Texture2D.LoadFromImage(srcImage);
        }
        else
        {
            Log.Warning($"Image {texturePath} is invalid or missing!");
        }

        propTexCache.Add(assetName, texture);
        return texture;
    }

    /// <summary>
    /// Obtain the texture of a prop init. Works for normal props and tiles as props.
    /// May be cached
    /// </summary>
    /// <param name="propInit">The prop init whose data is used to obtain the texture.</param>
    /// <returns>The prop/tile texture, or null if the graphics file was invalid or not found.</returns>
    public RlManaged.Texture2D? GetPropTexture(Props.PropInit propInit)
    {
        if (propInit.PropFlags.HasFlag(Props.PropFlags.Tile))
            return GetTileTexture(propInit.Name);
        else
            return GetPropTexture(propInit.Name);
    }

    /// <summary>
    /// Obtain the texture of a tile asset. May be cached.
    /// </summary>
    /// <param name="assetName">The name of the tile asset.</param>
    /// <returns>The tile texture, or null if the graphics file was invalid or not found.</returns>
    public RlManaged.Texture2D? GetTileTexture(string assetName)
    {
        if (tileTexCache.TryGetValue(assetName, out RlManaged.Texture2D? texture))
            return texture;

        // find tile path
        // for some reason, previews for drought props are in cast data instead of in the Props folder
        // kind of annoying. so i just put those images in assets/internal
        string texturePath = GetFilePath(Path.Combine(RainEd.Instance.AssetDataPath, "Graphics"), assetName + ".png");
        if (!File.Exists(texturePath) && DrizzleCastMap.TryGetValue(assetName, out string? castPath))
        {
            texturePath = Path.Combine(Boot.AppDataPath, "assets", "internal", castPath!);
        }

        using var srcImage = RlManaged.Image.Load(texturePath);

        if (Raylib.IsImageReady(srcImage))
        {
            CropImage(srcImage);
            texture = RlManaged.Texture2D.LoadFromImage(srcImage);
        }
        else
        {
            Log.Warning($"Image {texturePath} is invalid or missing!");
        }

        tileTexCache.Add(assetName, texture);
        return texture;
    }

    private bool PackAtlas(TilePreviewAtlas atlas)
    {
        var activeRects = new Span<PackingRectangle>(atlas.rectangles, 0, atlas.rectangleCount);
        RectanglePacker.Pack(activeRects, out PackingRectangle bounds, PackingHints.FindBest, maxBoundsWidth: AtlasTextureWidth, maxBoundsHeight: AtlasTextureHeight);

        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > AtlasTextureWidth || bounds.Height > AtlasTextureHeight)
        {
            return false;
        }
        else
        {
            atlas.packWidth = bounds.X + bounds.Width;
            atlas.packHeight = bounds.Y + bounds.Height;
            atlas.curX = atlas.packWidth;
            atlas.curY = 0;
            var tex = atlas.texture.GlibTexture;

            {
                using var resetImg = RlManaged.Image.GenColor(AtlasTextureWidth, AtlasTextureHeight, Color.Blank);
                Raylib.UpdateTexture(atlas.texture, resetImg);
            }
            
            for (int i = 0; i < activeRects.Length; i++)
            {
                ref var rect = ref activeRects[i];
                atlas.idIndices[rect.Id] = i;

                var tileName = atlas.tiles[rect.Id];
                var tileImg = _loadedTilePreviews[tileName];
                System.Diagnostics.Debug.Assert(rect.Width - 2 == tileImg.Width && rect.Height - 2 == tileImg.Height);
                tex.UpdateFromImage(((Image)tileImg).image!, rect.X + 1, rect.Y + 1);
            }

            return true;
        }
    }

    private void AddTilePreview(string tileName)
    {
        if (_tilePreviewAtlases.Count == 0)
        {
            _tilePreviewAtlases.Add(new TilePreviewAtlas());
        }

        var atlas = _tilePreviewAtlases[^1];
        if (atlas.rectangleCount + 1 >= MaxRectsPerAtlas)
        {
            atlas = new TilePreviewAtlas();
            _tilePreviewAtlases.Add(atlas);
        }

        var tileImg = _loadedTilePreviews[tileName];

        // perform a super simple addition of the the image into the texture.
        // this doesn't do packing, it just adds it to the easiest to find empty space,
        // since packing is only done once per frame
        var newRect = new PackingRectangle(
            0, 0,
            (uint)tileImg.Width + 2, (uint)tileImg.Height + 2,
            id: atlas.rectangleCount
        );

        // ran out of space on this row, move to the next one
        if (newRect.Width >= AtlasTextureWidth || newRect.Height >= AtlasTextureHeight)
        {
            throw new Exception($"Tiles whose dimensions are larger than ({AtlasTextureWidth}, {AtlasTextureHeight}) pixels are not supported");
        }

        while (atlas.curX + newRect.Width >= AtlasTextureWidth)
        {
            atlas.curY += atlas.rowHeight;
            atlas.rowHeight = newRect.Height;
            atlas.curX = atlas.curY > atlas.packHeight ? 0 : atlas.packWidth;
        }

        // ran out of space for the entire atlas texture. try to call
        // rect packer. if rect packer can't fit all the textures into the one atlas,
        // create another atlas texture.
        // this will make it so that previous draw calls referring to the atlas texture
        // will be incorrect, but this condition does not happen often, and visual artifacts
        // last only one frame, so i don't feel like it's worth it to fix it.
        if (atlas.curY + newRect.Height >= AtlasTextureHeight)
        {
            newRect.X = 0;
            newRect.Y = 0;
            atlas.rectangles[atlas.rectangleCount] = newRect;
            atlas.rectangleCount++;
            atlas.tiles.Add(tileName);

            if (PackAtlas(atlas))
            {
                // force-packing was successful
                _tilePreviewRects.Add(tileName, (_tilePreviewAtlases.Count - 1, atlas.rectangleCount - 1));
                atlas.tiles.Add(tileName);
                return;

                // return here, so the code that quickly adds the tile preview to the atlas image isn't called
                // the preview was already added before packing
            }
            else
            {
                // space ran out, so we need a new texture atlas unfortunately
                atlas.rectangleCount--;
                atlas.tiles.RemoveAt(atlas.tiles.Count - 1);

                atlas = new TilePreviewAtlas();
                _tilePreviewAtlases.Add(atlas);
                newRect.Id = atlas.rectangleCount;
            }
        }
        
        newRect.X = atlas.curX;
        newRect.Y = atlas.curY;
        atlas.dirty = true;
        atlas.rectangles[atlas.rectangleCount] = newRect;
        atlas.rectangleCount++;

        atlas.curX += newRect.Width;
        atlas.rowHeight = Math.Max(atlas.rowHeight, newRect.Height);

        _tilePreviewRects.Add(tileName, (_tilePreviewAtlases.Count - 1, atlas.rectangleCount - 1));
        atlas.tiles.Add(tileName);

        // update atlas texture        
        System.Diagnostics.Debug.Assert(newRect.Width - 2 == tileImg.Width && newRect.Height - 2 == tileImg.Height);        
        atlas.texture.GlibTexture.UpdateFromImage(((Image)tileImg).image!, newRect.X + 1, newRect.Y + 1);

        atlas.idIndices[newRect.Id] = atlas.rectangleCount - 1;
    }

    /// <summary>
    /// Obtain the texture of a tile's preview texture. May be cached.
    /// </summary>
    /// <param name="tile">The tile whose data is used to obtain the preview texture.</param>
    /// <param name="texture">The texture to use to draw the tile.</param>
    /// <param name="rect">The UV rectangle</param>
    /// <returns>False if the graphics file was invalid or not found, otherwise true.</returns>
    public bool GetTilePreviewTexture(Tiles.Tile tile, out RlManaged.Texture2D? texture, out Rectangle? rect)
    {        
        // if texture already exists in cache,
        // return that instead of processing it again
        if (_tilePreviewRects.TryGetValue(tile.Name, out var cacheData))
        {
            var atlas = _tilePreviewAtlases[cacheData.atlasIndex];
            texture = atlas.texture;
            var packRect = atlas.rectangles[atlas.idIndices[cacheData.rectId]];
            rect = new Rectangle(packRect.X + 1, packRect.Y + 1, packRect.Width - 2, packRect.Height - 2);
            return true;
        }

        var graphicsPath = GetFilePath(Path.Combine(RainEd.Instance.AssetDataPath, "Graphics"), tile.Name + ".png");
        if (!File.Exists(graphicsPath) && DrizzleCastMap.TryGetValue(tile.Name, out string? castPath))
        {
            graphicsPath = Path.Combine(Boot.AppDataPath, "assets", "internal", castPath!);
        }

        using var fullImage = RlManaged.Image.Load(graphicsPath);
        if (Raylib.IsImageReady(fullImage))
        {
            CropImage(fullImage);

            var previewRect = new Rectangle(
                0,
                tile.ImageRowCount * 20 + tile.ImageYOffset,
                tile.Width * 16,
                tile.Height * 16
            );

            // clamp preview rect so that it won't be out of
            // bounds (raylib will stretch it if this is the case)
            if (previewRect.Width > fullImage.Width)
                previewRect.Width = fullImage.Width;
            
            if (previewRect.Y + previewRect.Height > fullImage.Height)
                previewRect.Height = fullImage.Height - previewRect.Y;

            var previewImage = RlManaged.Image.GenColor(tile.Width * 16, tile.Height * 16, Color.White);
            previewImage.Format(PixelFormat.UncompressedR8G8B8A8);

            if (previewRect.Height > 0) // thanks, huge tnak.
            {
                Raylib.ImageDraw(
                    previewImage,
                    fullImage,
                    previewRect,
                    new Rectangle(0, 0, previewRect.Width, previewRect.Height),
                    Color.White
                );
            }

            // convert black-and-white image to white-and-transparent, respectively
            for (int x = 0; x < previewImage.Width; x++)
            {
                for (int y = 0; y < previewImage.Height; y++)
                {
                    if (Raylib.GetImageColor(previewImage, x, y).Equals(new Color(255, 255, 255, 255)))
                    {
                        previewImage.DrawPixel(x, y, new Color(255, 25, 255, 0));
                    }
                    else
                    {
                        previewImage.DrawPixel(x, y, new Color(255, 255, 255, 255));
                    }
                }
            }

            //outTexture = RlManaged.Texture2D.LoadFromImage(previewImage);
            _loadedTilePreviews.Add(tile.Name, previewImage);
            AddTilePreview(tile.Name);

            cacheData = _tilePreviewRects[tile.Name];
            var atlas = _tilePreviewAtlases[cacheData.atlasIndex];
            texture = atlas.texture;
            var packRect = atlas.rectangles[atlas.idIndices[cacheData.rectId]];
            rect = new Rectangle(packRect.X + 1, packRect.Y + 1, packRect.Width - 2, packRect.Height - 2);
            return true;
        }
        else
        {
            // tile graphics could not be loaded
            Log.Warning($"Preview image {graphicsPath} is invalid or missing!");

            texture = null;
            rect = null;
            return false;
        }

        //previewTexCache.Add(tile.Name, outTexture);
        //return outTexture;
    }

    /// <summary>
    /// Crop out an image's bordering white pixels.
    /// Adobe Director auto-crops images when loading, which
    /// is why this is necessary.
    /// </summary>
    /// <param name="sourceImage">The image to crop.</param>
    /// <returns>True if the image was cropped, false if not.</returns>
    public static bool CropImage(RlManaged.Image sourceImage)
    {
        int imgMinX = -1;
        int imgMinY = -1;
        int imgMaxX = -1;
        int imgMaxY = -1;

        // find imgMinY
        for (int y = 0; y < sourceImage.Height; y++)
        {
            for (int x = 0; x < sourceImage.Width; x++)
            {
                var color = Raylib.GetImageColor(sourceImage, x, y);
                if (color.R != 255 || color.G != 255 || color.B != 255)
                {
                    imgMinY = y;
                    goto exitTopSearch;
                }
            }
        }
        exitTopSearch:;

        // find imgMinX
        for (int x = 0; x < sourceImage.Width; x++)
        {
            for (int y = 0; y < sourceImage.Height; y++)
            {
                var color = Raylib.GetImageColor(sourceImage, x, y);
                if (color.R != 255 || color.G != 255 || color.B != 255)
                {
                    imgMinX = x;
                    goto exitLeftSearch;
                }
            }
        }
        exitLeftSearch:;

        // find imgMaxY
        for (int y = sourceImage.Height - 1; y >= 0; y--)
        {
            for (int x = sourceImage.Width - 1; x >= 0; x--)
            {
                var color = Raylib.GetImageColor(sourceImage, x, y);
                if (color.R != 255 || color.G != 255 || color.B != 255)
                {
                    imgMaxY = y;
                    goto exitBottomSearch;
                }
            }
        }
        exitBottomSearch:;

        // find imgMaxX
        for (int x = sourceImage.Width - 1; x >= 0; x--)
        {
            for (int y = sourceImage.Height - 1; y >= 0; y--)
            {
                var color = Raylib.GetImageColor(sourceImage, x, y);
                if (color.R != 255 || color.G != 255 || color.B != 255)
                {
                    imgMaxX = x;
                    goto exitRightSearch;
                }
            }
        }
        exitRightSearch:;

        int width = imgMaxX - imgMinX + 1;
        int height = imgMaxY - imgMinY + 1;

        if (width == sourceImage.Width && height == sourceImage.Height)
        {
            return false;
        }
        else
        {
            Raylib.ImageCrop(ref sourceImage.Ref(), new Rectangle(imgMinX, imgMinY, width, height));
            return true;
        }
    }

    public void Maintenance()
    {
        foreach (var atlas in _tilePreviewAtlases)
        {
            if (atlas.dirty)
            {
                PackAtlas(atlas);
                atlas.dirty = false;
                atlas.curX = atlas.packWidth;
                atlas.curY = 0;
            }
        }
    }

    public void Test()
    {
        if (ImGui.Begin("Test window", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            for (int i = 0; i < _tilePreviewAtlases.Count; i++)
            {
                ImGuiExt.ImageSize(_tilePreviewAtlases[i].texture, AtlasTextureWidth, AtlasTextureHeight);
            }
        } ImGui.End();
    }
}