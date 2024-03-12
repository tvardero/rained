using Raylib_cs;
using rlImGui_cs;
using System.Numerics;
using ImGuiNET;
using Serilog;
using System.Runtime.InteropServices;
using Serilog.Core;
using System.Diagnostics;

namespace RainEd;

sealed class RainEd
{
    public const string Version = "b1.1.5"; 

    public static RainEd Instance = null!;

    public bool Running = true; // if false, Boot.cs will close the window
    private readonly Logger _logger;
    public static Logger Logger { get => Instance._logger; }

    private Level level;
    public readonly RlManaged.Texture2D LevelGraphicsTexture;
    private readonly EditorWindow editorWindow;
    private readonly ChangeHistory.ChangeHistory changeHistory;
    private bool ShowDemoWindow = false;

    private readonly string prefFilePath;
    public UserPreferences Preferences;
    public readonly Tiles.MaterialDatabase MaterialDatabase;
    public readonly Tiles.TileDatabase TileDatabase;
    public readonly EffectsDatabase EffectsDatabase;
    public readonly Light.LightBrushDatabase LightBrushDatabase;
    public readonly Props.PropDatabase PropDatabase;

    private string currentFilePath = string.Empty;

    public string CurrentFilePath { get => currentFilePath; }
    public Level Level { get => level; }
    public EditorWindow Window { get => editorWindow; }

    private string notification = "";
    private float notificationTime = 0f;
    private float notifFlash = 0f;
    private int timerDelay = 2;
    
    public ChangeHistory.ChangeHistory ChangeHistory { get => changeHistory; }

    private DrizzleRenderWindow? drizzleRenderWindow = null;
    private LevelResizeWindow? levelResizeWin = null;

    public LevelResizeWindow? LevelResizeWindow { get => levelResizeWin; }

    private double lastRopeUpdateTime = 0f;
    private float simTimeLeftOver = 0f;
    public float SimulationTimeRemainder { get => simTimeLeftOver; }

    public RainEd(string levelPath = "") {
        if (Instance != null)
            throw new Exception("Attempt to create more than one RainEd instance");
        
        Instance = this;

        // create serilog logger
        Directory.CreateDirectory(Path.Combine(Boot.AppDataPath, "logs"));
    
#if DEBUG
        _logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();
#else
        _logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(Boot.AppDataPath, "logs", "log.txt"), rollingInterval: RollingInterval.Day)
            .CreateLogger();
#endif
        Logger.Information("Rained {Version} started", Version);
        Logger.Information("App data located in {AppDataPath}", Boot.AppDataPath);

        // load user preferences
        prefFilePath = Path.Combine(Boot.AppDataPath, "preferences.json");

        if (File.Exists(prefFilePath))
        {
            try
            {
                Preferences = UserPreferences.LoadFromFile(prefFilePath);
            }
            catch (Exception e)
            {
                Logger.Error("Failed to load user preferences!\n{ErrorMessage}", e);
                Preferences = new UserPreferences();
                ShowNotification("Failed to load preferences");
            }
        }
        else
        {
            // first-time
            Preferences = new UserPreferences();
        }

        // load asset database
        Logger.Information("Initializing materials database...");
        MaterialDatabase = new Tiles.MaterialDatabase();

        Logger.Information("Initializing tile database...");
        TileDatabase = new Tiles.TileDatabase();

        Logger.Information("Initializing effects database...");
        EffectsDatabase = new EffectsDatabase();

        Logger.Information("Initializing light brush database...");
        LightBrushDatabase = new Light.LightBrushDatabase();

        Logger.Information("Initializing prop database...");
        PropDatabase = new Props.PropDatabase(TileDatabase);
        
        if (levelPath.Length > 0)
        {
            Logger.Information("Boot load " + levelPath);
            level = LevelSerialization.Load(levelPath);
        }
        else
        {
            Logger.Information("Boot load default level");
            level = Level.NewDefaultLevel();
        }

        LevelGraphicsTexture = RlManaged.Texture2D.Load(Path.Combine(Boot.AppDataPath,"assets","level-graphics.png"));

        Logger.Information("Initializing change history...");
        changeHistory = new ChangeHistory.ChangeHistory();

        Logger.Information("Creating editor window...");
        editorWindow = new EditorWindow();

        UpdateTitle();
        RegisterShortcuts();

        // apply preferences
        Raylib.SetWindowSize(Preferences.WindowWidth, Preferences.WindowHeight);
        if (Preferences.WindowMaximized)
        {
            Raylib.SetWindowState(ConfigFlags.MaximizedWindow);
        }
        ShortcutsWindow.IsWindowOpen = Preferences.ViewKeyboardShortcuts;

        Logger.Information("Boot successful!");
        lastRopeUpdateTime = Raylib.GetTime();
    }

    public void Shutdown()
    {
        // save user preferences
        editorWindow.SavePreferences(Preferences);
        Preferences.ViewKeyboardShortcuts = ShortcutsWindow.IsWindowOpen;

        Preferences.WindowWidth = Raylib.GetScreenWidth();
        Preferences.WindowHeight = Raylib.GetScreenHeight();
        Preferences.WindowMaximized = Raylib.IsWindowMaximized();
        
        UserPreferences.SaveToFile(Preferences, prefFilePath);
    }

    public void ShowNotification(string msg)
    {
        if (notification == "" || notificationTime != 3f)
        {
            notification = msg;
        }
        else
        {
            notification += "\n" + msg;
        }
        
        notificationTime = 3f;
        notifFlash = 0f;
    }

    public void ShowPathInSystemBrowser(string path, bool reveal)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (reveal)
                    Process.Start("explorer.exe", "/select," + path);
                else
                    Process.Start("explorer.exe", path);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // I can't test if this actually works as intended
                if (reveal)
                    Process.Start("open", "-R " + path);
                else
                    Process.Start("open", path);
            }
            else // assume Linux
            {
                if (reveal)
                    Process.Start("xdg-open", Path.GetDirectoryName(path)!);
                else
                    Process.Start("xdg-open", path);
            }
        }
        catch (Exception e)
        {
            Logger.Error("Could not show path '{Path}':\n{Error}", e);
            ShowNotification("Could not open the system file browser");
        }
    }

    private void LoadLevel(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Logger.Information("Loading level {Path}...", path);

            editorWindow.UnloadView();

            try
            {
                level = LevelSerialization.Load(path);
                ReloadLevel();
                currentFilePath = path;
                UpdateTitle();

                Logger.Information("Done!");
            }
            catch (Exception e)
            {
                Logger.Error("Error loading file {Path}:\n{ErrorMessage}", path, e);
                ShowNotification("Could not load level");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            editorWindow.ReloadLevel();
            editorWindow.LoadView();
        }
    }

    private void SaveLevel(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Logger.Information("Saving level to {Path}...", path);

            editorWindow.FlushDirty();

            try
            {
                LevelSerialization.Save(path);
                currentFilePath = path;
                UpdateTitle();
                changeHistory.MarkUpToDate();
                Logger.Information("Done!");
                ShowNotification("Saved!");
            }
            catch (Exception e)
            {
                Logger.Error("Could not write level file:\n{ErrorMessage}", e);
                ShowNotification("Could not write level file");
            }
            
            if (promptCallback is not null)
            {
                promptCallback();
            }
        }

        promptCallback = null;
    }

    public void ResizeLevel(int newWidth, int newHeight, int anchorX, int anchorY)
    {
        if (newWidth == level.Width && newHeight == level.Height) return;
        Logger.Information("Resizing level...");

        Window.FlushDirty();
        level.Resize(newWidth, newHeight, anchorX, anchorY);
        Window.ReloadLevel();
        changeHistory.Clear();
        Window.LevelRenderer.ReloadLevel();

        Logger.Information("Done!");
    }

    private void ReloadLevel()
    {
        editorWindow.ReloadLevel();
        changeHistory.Clear();
        changeHistory.MarkUpToDate();
        Window.LevelRenderer.ReloadLevel();
    }

    private Action? promptCallback;
    private bool promptUnsavedChanges;
    private bool promptUnsavedChangesCancelable;

    private void PromptUnsavedChanges(Action callback, bool canCancel = true)
    {
        promptUnsavedChangesCancelable = canCancel;

        if (changeHistory.HasChanges || (!canCancel && string.IsNullOrEmpty(currentFilePath)))
        {
            promptUnsavedChanges = true;
            promptCallback = callback;
        }
        else
        {
            callback();
        }
    }

    private void UpdateTitle()
    {
        var levelName =
            string.IsNullOrEmpty(currentFilePath) ? "Untitled" :
            Path.GetFileNameWithoutExtension(currentFilePath);
        
        Raylib.SetWindowTitle($"Rained - {levelName}");
    }

#region Shortcuts
    public enum ShortcutID
    {
        NavUp, NavLeft, NavDown, NavRight,
        NewObject, SwitchLayer,

        New, Open, Save, SaveAs,
        Cut, Copy, Paste, Undo, Redo,

        Render,

        // Geometry
        ToggleLayer1, ToggleLayer2, ToggleLayer3,

        // Light
        ResetBrushTransform
    };

    private class KeyShortcut
    {
        public ShortcutID ID;
        public string ShortcutString;
        public ImGuiKey Key;
        public ImGuiModFlags Mods;
        public bool IsActivated = false;
        public bool AllowRepeat = false;

        public KeyShortcut(ShortcutID id, ImGuiKey key, ImGuiModFlags mods, bool allowRepeat = false)
        {
            // build shortcut string
            var str = new List<string>();

            if (mods.HasFlag(ImGuiModFlags.Ctrl))
                str.Add("Ctrl");
            
            if (mods.HasFlag(ImGuiModFlags.Shift))
                str.Add("Shift");
            
            if (mods.HasFlag(ImGuiModFlags.Alt))
                str.Add("Alt");
            
            if (mods.HasFlag(ImGuiModFlags.Super))
                str.Add("Super");
            
            str.Add(ImGui.GetKeyName(key));

            ShortcutString = string.Join('+', str);

            // initialize other properties
            ID = id;
            Key = key;
            Mods = mods;
            AllowRepeat = allowRepeat;
        }

        public bool IsKeyPressed()
            =>
                ImGui.IsKeyPressed(Key, AllowRepeat) &&
                (Mods.HasFlag(ImGuiModFlags.Ctrl) == ImGui.IsKeyDown(ImGuiKey.ModCtrl)) &&
                (Mods.HasFlag(ImGuiModFlags.Shift) == ImGui.IsKeyDown(ImGuiKey.ModShift)) &&
                (Mods.HasFlag(ImGuiModFlags.Alt) == ImGui.IsKeyDown(ImGuiKey.ModAlt)) &&
                (Mods.HasFlag(ImGuiModFlags.Super) == ImGui.IsKeyDown(ImGuiKey.ModSuper));
    }
    private readonly Dictionary<ShortcutID, KeyShortcut> keyShortcuts = new();

    public void RegisterKeyShortcut(ShortcutID id, ImGuiKey key, ImGuiModFlags mods, bool allowRepeat = false)
    {
        keyShortcuts.Add(id, new KeyShortcut(id, key, mods, allowRepeat));
    }

    public bool IsShortcutActivated(ShortcutID id)
        => keyShortcuts[id].IsActivated;

    public void ImGuiMenuItemShortcut(ShortcutID id, string name)
    {
        var shortcutData = keyShortcuts[id];
        if (ImGui.MenuItem(name, shortcutData.ShortcutString))
            shortcutData.IsActivated = true;
    }

    private void RegisterShortcuts()
    {
        RegisterKeyShortcut(ShortcutID.NavUp, ImGuiKey.W, ImGuiModFlags.None, true);
        RegisterKeyShortcut(ShortcutID.NavLeft, ImGuiKey.A, ImGuiModFlags.None, true);
        RegisterKeyShortcut(ShortcutID.NavDown, ImGuiKey.S, ImGuiModFlags.None, true);
        RegisterKeyShortcut(ShortcutID.NavRight, ImGuiKey.D, ImGuiModFlags.None, true);
        RegisterKeyShortcut(ShortcutID.NewObject, ImGuiKey.N, ImGuiModFlags.None, true);

        RegisterKeyShortcut(ShortcutID.New, ImGuiKey.N, ImGuiModFlags.Ctrl);
        RegisterKeyShortcut(ShortcutID.Open, ImGuiKey.O, ImGuiModFlags.Ctrl);
        RegisterKeyShortcut(ShortcutID.Save, ImGuiKey.S, ImGuiModFlags.Ctrl);
        RegisterKeyShortcut(ShortcutID.SaveAs, ImGuiKey.S, ImGuiModFlags.Ctrl | ImGuiModFlags.Shift);
        RegisterKeyShortcut(ShortcutID.Render, ImGuiKey.R, ImGuiModFlags.Ctrl);

        RegisterKeyShortcut(ShortcutID.Cut, ImGuiKey.X, ImGuiModFlags.Ctrl);
        RegisterKeyShortcut(ShortcutID.Copy, ImGuiKey.C, ImGuiModFlags.Ctrl);
        RegisterKeyShortcut(ShortcutID.Paste, ImGuiKey.V, ImGuiModFlags.Ctrl);
        RegisterKeyShortcut(ShortcutID.Undo, ImGuiKey.Z, ImGuiModFlags.Ctrl, true);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            RegisterKeyShortcut(ShortcutID.Redo, ImGuiKey.Y, ImGuiModFlags.Ctrl, true);
        else
            RegisterKeyShortcut(ShortcutID.Redo, ImGuiKey.Z, ImGuiModFlags.Ctrl | ImGuiModFlags.Shift, true);
        
        RegisterKeyShortcut(ShortcutID.ToggleLayer1, ImGuiKey.E, ImGuiModFlags.None);
        RegisterKeyShortcut(ShortcutID.ToggleLayer2, ImGuiKey.R, ImGuiModFlags.None);
        RegisterKeyShortcut(ShortcutID.ToggleLayer3, ImGuiKey.T, ImGuiModFlags.None);

        RegisterKeyShortcut(ShortcutID.ResetBrushTransform, ImGuiKey.R, ImGuiModFlags.None);
    }

    private void OpenLevelBrowser(FileBrowser.OpenMode openMode, Action<string> callback)
    {
        static bool levelCheck(string path, bool isRw)
        {
            return isRw;
        }

        FileBrowser.Open(openMode, callback, currentFilePath);
        FileBrowser.AddFilter("Rain World level file", levelCheck, ".txt");
    }

    private void HandleShortcuts()
    {
        // activate shortcuts on key press
        if (!ImGui.GetIO().WantTextInput)
        {
            foreach (var shortcut in keyShortcuts.Values)
            {
                if (shortcut.IsKeyPressed())
                    shortcut.IsActivated = true;
            }
        }

        if (IsShortcutActivated(ShortcutID.New))
        {
            PromptUnsavedChanges(() =>
            {
                Logger.Information("Load default level...");

                editorWindow.UnloadView();
                level.LightMap.Dispose();
                level = Level.NewDefaultLevel();
                ReloadLevel();
                editorWindow.LoadView();

                currentFilePath = string.Empty;
                UpdateTitle();

                Logger.Information("Done!");
            });
        }

        if (IsShortcutActivated(ShortcutID.Open))
        {
            PromptUnsavedChanges(() =>
            {
                OpenLevelBrowser(FileBrowser.OpenMode.Read, LoadLevel);
            });
        }

        if (IsShortcutActivated(ShortcutID.Save))
        {
            if (string.IsNullOrEmpty(currentFilePath))
                OpenLevelBrowser(FileBrowser.OpenMode.Write, SaveLevel);
            else
                SaveLevel(currentFilePath);
        }

        if (IsShortcutActivated(ShortcutID.SaveAs))
        {
            OpenLevelBrowser(FileBrowser.OpenMode.Write, SaveLevel);
        }

        if (IsShortcutActivated(ShortcutID.Undo))
        {
            Undo();
        }

        if (IsShortcutActivated(ShortcutID.Redo))
        {
            Redo();
        }

        if (IsShortcutActivated(ShortcutID.Render))
        {
            PromptUnsavedChanges(() =>
            {
                drizzleRenderWindow = new DrizzleRenderWindow();
            }, false);
        }
    }
#endregion

    public void Draw(float dt)
    {
        if (Raylib.WindowShouldClose())
            PromptUnsavedChanges(() => Running = false);
        
        Raylib.ClearBackground(Color.DarkGray);
        
        foreach (var shortcut in keyShortcuts.Values)
            shortcut.IsActivated = false;

        rlImGui.Begin();
        ImGui.DockSpaceOverViewport();

        // main menu bar
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                ImGuiMenuItemShortcut(ShortcutID.New, "New");
                ImGuiMenuItemShortcut(ShortcutID.Open, "Open");
                ImGuiMenuItemShortcut(ShortcutID.Save, "Save");
                ImGuiMenuItemShortcut(ShortcutID.SaveAs, "Save As...");

                ImGui.Separator();

                ImGuiMenuItemShortcut(ShortcutID.Render, "Render...");
                ImGui.MenuItem("Mass Render", false);

                ImGui.Separator();
                if (ImGui.MenuItem("Quit", "Alt+F4"))
                {
                    PromptUnsavedChanges(() => Running = false);
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Edit"))
            {
                ImGuiMenuItemShortcut(ShortcutID.Undo, "Undo");
                ImGuiMenuItemShortcut(ShortcutID.Redo, "Redo");
                //ImGui.Separator();
                //ImGuiMenuItemShortcut(ShortcutID.Cut, "Cut");
                //ImGuiMenuItemShortcut(ShortcutID.Copy, "Copy");
                //ImGuiMenuItemShortcut(ShortcutID.Paste, "Paste");
                ImGui.Separator();

                if (ImGui.MenuItem("Resize Level..."))
                {
                    levelResizeWin = new LevelResizeWindow();
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.MenuItem("Grid", null, editorWindow.LevelRenderer.ViewGrid))
                {
                    editorWindow.LevelRenderer.ViewGrid = !editorWindow.LevelRenderer.ViewGrid;
                }

                if (ImGui.MenuItem("Obscured Beams", null, editorWindow.LevelRenderer.ViewObscuredBeams))
                {
                    editorWindow.LevelRenderer.ViewObscuredBeams = !editorWindow.LevelRenderer.ViewObscuredBeams;
                }

                if (ImGui.MenuItem("Tile Heads", null, editorWindow.LevelRenderer.ViewTileHeads))
                {
                    editorWindow.LevelRenderer.ViewTileHeads = !editorWindow.LevelRenderer.ViewTileHeads;
                }

                ImGui.Separator();

                if (ImGui.MenuItem("Keyboard Shortcuts", null, ShortcutsWindow.IsWindowOpen))
                {
                    ShortcutsWindow.IsWindowOpen = !ShortcutsWindow.IsWindowOpen;
                }

                ImGui.Separator();
                
                if (ImGui.MenuItem("Show Data Folder..."))
                    ShowPathInSystemBrowser(Path.Combine(Boot.AppDataPath, "Data"), false);
                
                if (ImGui.MenuItem("Show Render Folder..."))
                    ShowPathInSystemBrowser(Path.Combine(Boot.AppDataPath, "Data", "Levels"), false);
                
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("About..."))
                {
                    AboutWindow.IsWindowOpen = true;
                }
                
                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }

        HandleShortcuts();

        UpdateRopeSimulation();
        editorWindow.Render(dt);

        if (ImGui.IsKeyPressed(ImGuiKey.F1))
            ShowDemoWindow = !ShowDemoWindow;
        
        // this is how imgui is documented
        // you see what it can do and when i want to know how it does that,
        // i ctrl+f imgui_demo.cpp.
        // I guess it works?
        if (ShowDemoWindow)
        {
            ImGui.ShowDemoWindow(ref ShowDemoWindow);
        }

        // render level browser
        FileBrowser.Render();

        // render drizzle render, if in progress
        if (drizzleRenderWindow is not null)
        {
            // if this returns true, the render window had closed
            if (drizzleRenderWindow.DrawWindow())
            {
                drizzleRenderWindow.Dispose();
                drizzleRenderWindow = null;

                // the whole render process allocates ~1 gb of memory
                // so, try to free all that
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForFullGCComplete();
            }
        }

        // render level resize window
        if (levelResizeWin is not null)
        {
            levelResizeWin.DrawWindow();
            if (!levelResizeWin.IsWindowOpen) levelResizeWin = null;
        }
        
        // notification window
        if (notificationTime > 0f) {
            ImGuiWindowFlags windowFlags =
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
            
            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            const float pad = 10f;

            Vector2 windowPos = new(
                viewport.WorkPos.X + pad,
                viewport.WorkPos.Y + viewport.WorkSize.Y - pad
            );
            Vector2 windowPosPivot = new(0f, 1f);
            ImGui.SetNextWindowPos(windowPos, ImGuiCond.Always, windowPosPivot);

            var flashValue = (float) (Math.Sin(Math.Min(notifFlash, 0.25f) * 16 * Math.PI) + 1f) / 2f;
            var windowBg = ImGui.GetStyle().Colors[(int) ImGuiCol.WindowBg];

            if (flashValue > 0.5f)
                ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(flashValue, flashValue, flashValue, windowBg.W));
            else
                ImGui.PushStyleColor(ImGuiCol.WindowBg, windowBg);
            
            if (ImGui.Begin("Notification", windowFlags))
                ImGui.TextUnformatted(notification);
            ImGui.End();

            ImGui.PopStyleColor();

            if (timerDelay == 0)
            {
                notificationTime -= dt;
                notifFlash += dt;
            }
        }

        ShortcutsWindow.ShowWindow();
        AboutWindow.ShowWindow();

        // prompt unsaved changes
        if (promptUnsavedChanges)
        {
            promptUnsavedChanges = false;
            ImGui.OpenPopup("Unsaved Changes");

            // center popup modal
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        }

        bool unused = true;
        if (ImGui.BeginPopupModal("Unsaved Changes", ref unused, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            if (promptUnsavedChangesCancelable)
            {
                ImGui.Text("Do you want to save your changes before proceeding?");
            }
            else
            {
                ImGui.Text("You must save before proceeding.\nDo you want to save now?");
            }

            if (ImGui.Button("Yes") || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.Space))
            {
                ImGui.CloseCurrentPopup();

                // unsaved change callback is run in SaveLevel
                if (string.IsNullOrEmpty(currentFilePath))
                    OpenLevelBrowser(FileBrowser.OpenMode.Write, SaveLevel);
                else
                    SaveLevel(currentFilePath);
            }

            ImGui.SameLine();
            if (ImGui.Button("No") || (!promptUnsavedChangesCancelable && ImGui.IsKeyPressed(ImGuiKey.Escape)))
            {
                ImGui.CloseCurrentPopup();

                if (promptUnsavedChangesCancelable)
                {
                    if (promptCallback is not null)
                    {
                        promptCallback();
                        promptCallback = null;
                    }
                }
            }

            if (promptUnsavedChangesCancelable)
            {
                ImGui.SameLine();
                if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    ImGui.CloseCurrentPopup();
                    promptCallback = null;
                }
            }

            ImGui.EndPopup();
        }

        rlImGui.End();

        if (timerDelay > 0)
            timerDelay--;
    }

    public void UpdateRopeSimulation()
    {
        double nowTime = Raylib.GetTime();
        double stepTime = 1.0 / 30.0;

        for (int i = 0; nowTime >= lastRopeUpdateTime + stepTime; i++)
        {
            lastRopeUpdateTime += stepTime;
            
            // tick rope simulation
            foreach (var prop in level.Props)
            {
                prop.TickRopeSimulation();
            }

            // break if too many iterations in one frame
            if (i == 8)
            {
                lastRopeUpdateTime += stepTime * Math.Floor(nowTime - lastRopeUpdateTime);
                break;
            }
        }

        simTimeLeftOver = (float)((nowTime - lastRopeUpdateTime) / stepTime);

        foreach (var prop in level.Props)
        {
            if (prop.Rope is not null && prop.Rope.Simulate)
                prop.Rope.SimulationTimeRemainder = simTimeLeftOver;
        }
    }
    
    public void Undo() => changeHistory.Undo();
    public void Redo() => changeHistory.Redo();
}