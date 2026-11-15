-- Theme constants for LuaTracker

THEME = {
    -- Backgrounds
    bg = {128/255, 128/255, 128/255, 1},           -- 128 grayscale (main bg)
    panelBg = {64/255, 64/255, 64/255, 1},         -- 64 grayscale (tracker, lists)

    -- Borders and text
    border = {1, 1, 1, 1},                          -- White
    text = {1, 1, 1, 1},                            -- White
    textDim = {0.6, 0.6, 0.6, 1},                   -- Dimmed text

    -- Selection
    selection = {16/255, 192/255, 255/255, 0.5},   -- RGBA(16, 192, 255, 128)
    selectionSolid = {16/255, 192/255, 255/255, 1},

    -- Buttons
    buttonNormal = {0.5, 0.5, 0.5, 1},
    buttonHover = {0.75, 0.75, 0.75, 1},
    buttonPressed = {0.25, 0.25, 0.25, 1}
}

-- Helper function to set color from theme
function setColor(colorTable)
    love.graphics.setColor(colorTable[1], colorTable[2], colorTable[3], colorTable[4])
end

-- Draw a panel with border
function drawPanel(x, y, w, h, usePanelBg)
    if usePanelBg then
        setColor(THEME.panelBg)
    else
        setColor(THEME.bg)
    end
    love.graphics.rectangle("fill", x, y, w, h)
    setColor(THEME.border)
    love.graphics.rectangle("line", x, y, w, h)
end

-- Format number as hex with specified digits
function toHex(num, digits)
    if num == nil then
        return string.rep("-", digits)
    end
    return string.format("%0" .. digits .. "X", num)
end

-- Convert MIDI note number to note name (C-0 to B-9)
function noteToString(note)
    if note == nil then
        return "---"
    elseif note == -1 then
        return "[_]"  -- Stop note
    end
    local names = {"C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-"}
    local octave = math.floor(note / 12)
    local name = names[(note % 12) + 1]
    return name .. octave
end

-- Convert note string to MIDI note number
function stringToNote(str)
    if str == "---" or str == "" then
        return nil
    elseif str == "[_]" then
        return -1
    end
    local names = {["C"]=0, ["D"]=2, ["E"]=4, ["F"]=5, ["G"]=7, ["A"]=9, ["B"]=11}
    local note = str:sub(1, 1):upper()
    local sharp = str:sub(2, 2) == "#"
    local octave = tonumber(str:sub(sharp and 3 or 2, sharp and 3 or 2)) or 0
    local base = names[note] or 0
    if sharp then base = base + 1 end
    return octave * 12 + base
end
