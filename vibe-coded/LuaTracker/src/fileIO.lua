-- File I/O for LuaTracker
-- Binary formats: .ltm (module) and .ltp (pattern)

FileIO = {}

-- Magic bytes for file identification
FileIO.MODULE_MAGIC = "LTM2"
FileIO.PATTERN_MAGIC = "LTP2"

-- Legacy magic bytes (v1 format, readable but not written)
FileIO.MODULE_MAGIC_V1 = "LTM1"
FileIO.PATTERN_MAGIC_V1 = "LTP1"

-- Write a 32-bit integer (little endian)
local function writeInt32(file, value)
    file:write(string.char(
        value % 256,
        math.floor(value / 256) % 256,
        math.floor(value / 65536) % 256,
        math.floor(value / 16777216) % 256
    ))
end

-- Read a 32-bit integer (little endian)
local function readInt32(file)
    local bytes = file:read(4)
    if not bytes or #bytes < 4 then return nil end
    local b1, b2, b3, b4 = bytes:byte(1, 4)
    return b1 + b2 * 256 + b3 * 65536 + b4 * 16777216
end

-- Write a 16-bit integer (little endian)
local function writeInt16(file, value)
    file:write(string.char(value % 256, math.floor(value / 256) % 256))
end

-- Read a 16-bit integer (little endian)
local function readInt16(file)
    local bytes = file:read(2)
    if not bytes or #bytes < 2 then return nil end
    local b1, b2 = bytes:byte(1, 2)
    return b1 + b2 * 256
end

-- Write a byte
local function writeByte(file, value)
    file:write(string.char(value or 0))
end

-- Read a byte
local function readByte(file)
    local byte = file:read(1)
    if not byte then return nil end
    return byte:byte()
end

-- Write a string with length prefix
local function writeString(file, str)
    str = str or ""
    writeInt16(file, #str)
    file:write(str)
end

-- Read a string with length prefix
local function readString(file)
    local len = readInt16(file)
    if not len then return nil end
    return file:read(len)
end

-- Write a note (6 bytes: note byte, presence mask, instrument, command, op1, op2)
local function writeNote(file, note)
    -- Byte 1: Note value (0-127 for MIDI notes, 0xFE for stop, 0xFF for empty)
    if not note or note.note == nil then
        writeByte(file, 0xFF)
    elseif note.note == -1 then
        writeByte(file, 0xFE)
    else
        writeByte(file, note.note)
    end

    -- Byte 2: Presence bitmask for the 4 remaining fields
    -- bit 0 = instrument present, bit 1 = command, bit 2 = op1, bit 3 = op2
    local mask = 0
    local inst = note and note.instrument
    local cmd = note and note.command
    local op1 = note and note.operand1
    local op2 = note and note.operand2
    if inst ~= nil then mask = mask + 1 end
    if cmd ~= nil then mask = mask + 2 end
    if op1 ~= nil then mask = mask + 4 end
    if op2 ~= nil then mask = mask + 8 end
    writeByte(file, mask)

    -- Bytes 3-6: field values (0 when nil, value is ignored on read if bit is unset)
    writeByte(file, inst or 0)
    writeByte(file, cmd or 0)
    writeByte(file, op1 or 0)
    writeByte(file, op2 or 0)
end

-- Read a note (v2 format: 6 bytes with presence mask)
local function readNote(file)
    local noteVal = readByte(file)
    if noteVal == nil then return nil end

    local note = {}

    if noteVal == 0xFF then
        note.note = nil
    elseif noteVal == 0xFE then
        note.note = -1  -- Stop
    else
        note.note = noteVal
    end

    local mask = readByte(file)

    local inst = readByte(file)
    if mask % 2 >= 1 then note.instrument = inst end

    local cmd = readByte(file)
    if math.floor(mask / 2) % 2 >= 1 then note.command = cmd end

    local op1 = readByte(file)
    if math.floor(mask / 4) % 2 >= 1 then note.operand1 = op1 end

    local op2 = readByte(file)
    if math.floor(mask / 8) % 2 >= 1 then note.operand2 = op2 end

    -- Return nil for completely empty notes
    if note.note == nil and note.instrument == nil and note.command == nil
        and note.operand1 == nil and note.operand2 == nil then
        return nil
    end

    return note
end

-- Read a note from v1 format (5 bytes, 0xFF ambiguity for nil)
local function readNoteV1(file)
    local noteVal = readByte(file)
    if noteVal == nil then return nil end

    local note = {}

    if noteVal == 0xFF then
        note.note = nil
    elseif noteVal == 0xFE then
        note.note = -1
    else
        note.note = noteVal
    end

    local inst = readByte(file)
    if inst ~= 0xFF then note.instrument = inst end

    local cmd = readByte(file)
    if cmd ~= 0xFF then note.command = cmd end

    local op1 = readByte(file)
    if op1 ~= 0xFF then note.operand1 = op1 end

    local op2 = readByte(file)
    if op2 ~= 0xFF then note.operand2 = op2 end

    if note.note == nil and note.instrument == nil and note.command == nil
        and note.operand1 == nil and note.operand2 == nil then
        return nil
    end

    return note
end

-- Save module to .ltm file
function FileIO.saveModule(filepath)
    local file = io.open(filepath, "wb")
    if not file then
        return false, "Could not open file for writing"
    end

    -- Header
    file:write(FileIO.MODULE_MAGIC)
    writeInt16(file, Tracker.tempo)
    writeInt16(file, Tracker.patternLength)
    writeByte(file, Tracker.channelCount)

    -- Instruments
    writeInt16(file, #Tracker.instruments)
    for _, inst in ipairs(Tracker.instruments) do
        writeString(file, inst.name)
        writeString(file, inst.path)
    end

    -- Patterns
    writeInt16(file, #Tracker.patterns)
    for _, pattern in ipairs(Tracker.patterns) do
        for ch = 1, Tracker.channelCount do
            for row = 1, Tracker.patternLength do
                writeNote(file, pattern.channels[ch].notes[row])
            end
        end
    end

    -- Play order
    writeInt16(file, #Tracker.playOrder)
    for _, idx in ipairs(Tracker.playOrder) do
        writeInt16(file, idx)
    end

    file:close()
    return true
end

-- Load module from .ltm file
function FileIO.loadModule(filepath)
    local file = io.open(filepath, "rb")
    if not file then
        return false, "Could not open file for reading"
    end

    -- Check magic
    local magic = file:read(4)
    local noteReader
    if magic == FileIO.MODULE_MAGIC then
        noteReader = readNote
    elseif magic == FileIO.MODULE_MAGIC_V1 then
        noteReader = readNoteV1
    else
        file:close()
        return false, "Invalid module file format"
    end

    -- Header
    Tracker.tempo = readInt16(file) or 120
    Tracker.patternLength = readInt16(file) or 64
    Tracker.channelCount = readByte(file) or 4

    -- Reset channel sources
    Tracker.channelSources = {}
    for i = 1, Tracker.channelCount do
        Tracker.channelSources[i] = nil
    end

    -- Instruments (paths only, need to reload)
    local instrumentPaths = {}
    local instCount = readInt16(file) or 0
    Tracker.instruments = {}
    for i = 1, instCount do
        local name = readString(file)
        local path = readString(file)
        table.insert(instrumentPaths, {name = name, path = path})
    end

    -- Try to reload instruments
    for _, inst in ipairs(instrumentPaths) do
        local success = pcall(function()
            Tracker:loadInstrument(inst.path)
        end)
        if not success then
            -- Add placeholder
            table.insert(Tracker.instruments, {name = inst.name .. " (missing)", path = inst.path, source = nil})
        end
    end

    -- Patterns
    Tracker.patterns = {}
    local patternCount = readInt16(file) or 0
    for p = 1, patternCount do
        local pattern = {channels = {}}
        for ch = 1, Tracker.channelCount do
            pattern.channels[ch] = {notes = {}}
            for row = 1, Tracker.patternLength do
                pattern.channels[ch].notes[row] = noteReader(file) or Tracker:createEmptyNote()
            end
        end
        table.insert(Tracker.patterns, pattern)
    end

    -- Play order
    Tracker.playOrder = {}
    local orderCount = readInt16(file) or 0
    for i = 1, orderCount do
        local idx = readInt16(file) or 0
        table.insert(Tracker.playOrder, idx)
    end

    -- Ensure at least one pattern and play order entry
    if #Tracker.patterns == 0 then
        Tracker:addPattern()
    end
    if #Tracker.playOrder == 0 then
        Tracker.playOrder = {0}
    end

    file:close()
    return true
end

-- Export single pattern to .ltp file
function FileIO.exportPattern(filepath, patternIndex)
    local pattern = Tracker.patterns[patternIndex + 1]
    if not pattern then
        return false, "Pattern does not exist"
    end

    local file = io.open(filepath, "wb")
    if not file then
        return false, "Could not open file for writing"
    end

    -- Header
    file:write(FileIO.PATTERN_MAGIC)
    writeInt16(file, Tracker.patternLength)
    writeByte(file, Tracker.channelCount)

    -- Pattern data
    for ch = 1, Tracker.channelCount do
        for row = 1, Tracker.patternLength do
            writeNote(file, pattern.channels[ch].notes[row])
        end
    end

    file:close()
    return true
end

-- Import pattern from .ltp file
function FileIO.importPattern(filepath)
    local file = io.open(filepath, "rb")
    if not file then
        return false, "Could not open file for reading"
    end

    -- Check magic
    local magic = file:read(4)
    local noteReader
    if magic == FileIO.PATTERN_MAGIC then
        noteReader = readNote
    elseif magic == FileIO.PATTERN_MAGIC_V1 then
        noteReader = readNoteV1
    else
        file:close()
        return false, "Invalid pattern file format"
    end

    -- Header
    local length = readInt16(file) or 64
    local channels = readByte(file) or 4

    -- Create new pattern
    local pattern = {channels = {}}

    -- Read data (adapt to current tracker settings)
    for ch = 1, channels do
        pattern.channels[ch] = {notes = {}}
        for row = 1, length do
            local note = noteReader(file) or Tracker:createEmptyNote()
            if row <= Tracker.patternLength then
                pattern.channels[ch].notes[row] = note
            end
        end
        -- Fill remaining rows if pattern is shorter
        for row = length + 1, Tracker.patternLength do
            pattern.channels[ch].notes[row] = Tracker:createEmptyNote()
        end
    end

    -- Add missing channels if current tracker has more
    for ch = channels + 1, Tracker.channelCount do
        pattern.channels[ch] = {notes = {}}
        for row = 1, Tracker.patternLength do
            pattern.channels[ch].notes[row] = Tracker:createEmptyNote()
        end
    end

    table.insert(Tracker.patterns, pattern)
    file:close()
    return true, #Tracker.patterns - 1  -- Return 0-based index
end

return FileIO
