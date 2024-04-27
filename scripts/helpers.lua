local module = {}

---Perform standard path autotiling
---@param tileTable {ld: string, lu: string, rd: string, ru: string, vertical: string, horizontal: string}
---@param layer integer
---@param segments PathSegment[]
---@param startIndex integer? The index of the starting segment. Defaults to 1
---@param endIndex integer? The number of segments to place. Defaults to the length of the segment array
---@param forceModifier ForceModifier
function module.autotilePath(tileTable, layer, segments, forceModifier, startIndex, endIndex)
    for i=startIndex or 1, endIndex or #segments do
        local seg = segments[i]

        -- turns
        if seg.left and seg.down then
            rained.placeTile(tileTable.ld, layer, seg.x, seg.y, forceModifier)
        elseif seg.left and seg.up then
            rained.placeTile(tileTable.lu, layer, seg.x, seg.y, forceModifier)
        elseif seg.right and seg.down then
            rained.placeTile(tileTable.rd, layer, seg.x, seg.y, forceModifier)
        elseif seg.right and seg.up then
            rained.placeTile(tileTable.ru, layer, seg.x, seg.y, forceModifier)
        
        -- straight
        elseif seg.down or seg.up then
            rained.placeTile(
                tileTable.vertical,
                layer, seg.x, seg.y, forceModifier
            )
        elseif seg.right or seg.left then
            rained.placeTile(
                tileTable.horizontal,
                layer, seg.x, seg.y, forceModifier
            )
        end
    end
end

-- this data is for the patternBox helper function.
-- adapted from tileEditor.lingo
local patterns = {
    {tiles = {"A"}, upper = "dense", lower = "dense", tall = 1, freq = 5},
    {tiles = {"B1"}, upper = "espaced", lower = "dense", tall = 1, freq = 5},
    {tiles = {"B2"}, upper = "dense", lower = "espaced", tall = 1, freq = 5},
    {tiles = {"B3"}, upper = "ospaced", lower = "dense", tall = 1, freq = 5},
    {tiles = {"B4"}, upper = "dense", lower = "ospaced", tall = 1, freq = 5},
    {tiles = {"C1"}, upper = "espaced", lower = "espaced", tall = 1, freq = 5},
    {tiles = {"C2"}, upper = "ospaced", lower = "ospaced", tall = 1, freq = 5},
    {tiles = {"E1"}, upper = "ospaced", lower = "espaced", tall = 1, freq = 5},
    {tiles = {"E2"}, upper = "espaced", lower = "ospaced", tall = 1, freq = 5},
    {tiles = {"F1"}, upper = "dense", lower = "dense", tall = 2, freq = 1},
    {tiles = {"F2"}, upper = "dense", lower = "dense", tall = 2, freq = 1},
    {tiles = {"F1", "F2"}, upper = "dense", lower = "dense", tall = 2, freq = 5},
    {tiles = {"F3"}, upper = "dense", lower = "dense", tall = 2, freq = 5},
    {tiles = {"F4"}, upper = "dense", lower = "dense", tall = 2, freq = 5},
    {tiles = {"G1", "G2"}, upper = "dense", lower = "ospaced", tall = 2, freq = 5},
    {tiles = {"I"}, upper = "espaced", lower = "dense", tall = 1, freq = 4},
    {tiles = {"J1"}, upper = "ospaced", lower = "ospaced", tall = 2, freq = 1},
    {tiles = {"J2"}, upper = "ospaced", lower = "ospaced", tall = 2, freq = 1},
    {tiles = {"J1", "J2"}, upper = "ospaced", lower = "ospaced", tall = 2, freq = 2},
    {tiles = {"J3"}, upper = "espaced", lower = "espaced", tall = 2, freq = 1},
    {tiles = {"J4"}, upper = "espaced", lower = "espaced", tall = 2, freq = 1},
    {tiles = {"J3", "J4"}, upper = "espaced", lower = "espaced", tall = 2, freq = 2},
    {tiles = {"B1", "I"}, upper = "espaced", lower = "dense", tall = 1, freq = 2}
}

---The algorithm used to generate SH pattern box, SH grate box, and Alt Grate Box.
---Adapted from the original Lingo code.
---@param prefix string The string to prepend to the tile name when placing.
---@param layer integer The layer to tile.
---@param left integer The left side of the rectangle.
---@param top integer The top side of the rectangle.
---@param right integer The right side of the rectangle.
---@param bottom integer The bottom side of the rectangle.
---@param forceModifier ForceModifier
function module.patternBox(prefix, layer, left, top, right, bottom, forceModifier)
    -- place corner tiles
    rained.placeTile("Block Corner NW", layer, left, top, forceModifier)
    rained.placeTile("Block Corner NE", layer, right, top, forceModifier)
    rained.placeTile("Block Corner SE", layer, right, bottom, forceModifier)
    rained.placeTile("Block Corner SW", layer, left, bottom, forceModifier)

    -- fill sides
    for x = left + 1, right - 1 do
        rained.placeTile("Block Edge N", layer, x, top, forceModifier)
        rained.placeTile("Block Edge S", layer, x, bottom, forceModifier)
    end

    for y = top + 1, bottom - 1 do
        rained.placeTile("Block Edge W", layer, left, y, forceModifier)
        rained.placeTile("Block Edge E", layer, right, y, forceModifier)
    end

    -- the following code is translated straight from the lingo code
    local py = top + 1
    local currentPattern = patterns[math.random(#patterns)]

    while py < bottom do
        local possiblePatterns = {}

        for q=1, #patterns do
            if patterns[q].upper == currentPattern.lower and py + patterns[q].tall < bottom + 1 then
                for _=1, patterns[q].freq do
                    possiblePatterns[#possiblePatterns+1] = q
                end
            end
        end

        currentPattern = patterns[possiblePatterns[math.random(#possiblePatterns)]]
        local tl = math.random(#currentPattern.tiles)

        for px = left + 1, right - 1 do
            tl = tl + 1
            if tl > #currentPattern.tiles then
                tl = 1
            end

            rained.placeTile(prefix .. currentPattern.tiles[tl], layer, px, py, forceModifier)
        end

        py = py + currentPattern.tall
    end
end

return module