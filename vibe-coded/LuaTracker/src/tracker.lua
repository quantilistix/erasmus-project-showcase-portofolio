-- Core tracker data model and playback logic

Tracker = {
    -- Module data
    patterns = {},          -- Array of pattern definitions
    playOrder = {},         -- Array of pattern indices defining play sequence
    instruments = {},       -- Array of loaded instruments

    -- Settings
    tempo = 120,            -- BPM
    patternLength = 64,     -- Rows per pattern (0x00 to 0x3F)
    channelCount = 4,       -- Number of channels

    -- Playback state
    playing = false,
    currentOrderIndex = 1,  -- Current position in playOrder
    currentRow = 0,         -- Current row in pattern
    rowTimer = 0,           -- Timer for row advancement

    -- Channel state (for audio)
    channelSources = {},      -- Currently playing source per channel
    channelInstruments = {},  -- Instrument index per channel (for waveform)

    -- Editor state
    selectedInstrument = 1,
    currentOctave = 4,      -- Default octave for note input
    cursorRow = 0,
    cursorChannel = 1,
    cursorField = 1,        -- 1=note, 2=instrument, 3=cmd, 4=op1, 5=op2
    viewOffset = 0,         -- Scroll offset for pattern view
}

-- Initialize tracker with default empty pattern
function Tracker:init()
    self.patterns = {}
    self.playOrder = {0}
    self.instruments = {}
    self.channelSources = {}
    self.channelVolumes = {}  -- Per-channel volume (0-255 for L and R)
    self.playing = false
    self.currentOrderIndex = 1
    self.currentRow = 0
    self.rowTimer = 0
    self.cursorRow = 0
    self.cursorChannel = 1
    self.cursorField = 1

    for i = 1, self.channelCount do
        self.channelSources[i] = nil
        self.channelInstruments[i] = nil
        self.channelVolumes[i] = {left = 255, right = 255}
    end

    -- Initialize command processor
    if Commands then
        Commands:init()
    end

    -- Create one empty pattern
    self:addPattern()
end

-- Create a new empty pattern
function Tracker:addPattern()
    local pattern = {channels = {}}
    for ch = 1, self.channelCount do
        pattern.channels[ch] = {notes = {}}
        for row = 1, self.patternLength do
            pattern.channels[ch].notes[row] = self:createEmptyNote()
        end
    end
    table.insert(self.patterns, pattern)
    return #self.patterns - 1  -- Return 0-based index
end

-- Create an empty note
function Tracker:createEmptyNote()
    return {
        note = nil,
        instrument = nil,
        command = nil,
        operand1 = nil,
        operand2 = nil
    }
end

-- Create a stop note
function Tracker:createStopNote()
    return {
        note = -1,
        instrument = nil,
        command = nil,
        operand1 = nil,
        operand2 = nil
    }
end

-- Get note at specific position
function Tracker:getNote(patternIdx, channel, row)
    local pattern = self.patterns[patternIdx + 1]  -- Convert 0-based to 1-based
    if pattern and pattern.channels[channel] then
        return pattern.channels[channel].notes[row + 1]  -- Convert 0-based to 1-based
    end
    return nil
end

-- Set note at specific position
function Tracker:setNote(patternIdx, channel, row, note)
    local pattern = self.patterns[patternIdx + 1]
    if pattern and pattern.channels[channel] then
        pattern.channels[channel].notes[row + 1] = note
    end
end

-- Get current pattern index from play order
function Tracker:getCurrentPatternIndex()
    return self.playOrder[self.currentOrderIndex] or 0
end

-- Load instrument from file (supports paths outside Love2D sandbox)
function Tracker:loadInstrument(path)
    local source = nil
    local soundData = nil
    local name = path:match("([^/\\]+)$") or path

    -- First try Love2D's filesystem (for paths in save/source directory)
    local success, result = pcall(function()
        local sd = love.sound.newSoundData(path)
        return sd, love.audio.newSource(sd)
    end)

    if success and result then
        soundData = result
        source = select(2, pcall(function() return love.audio.newSource(result) end))
        if not source then
            source = love.audio.newSource(path, "static")
        end
    else
        -- Try loading from external filesystem
        local file = io.open(path, "rb")
        if file then
            local data = file:read("*all")
            file:close()

            if data and #data > 0 then
                local fileDataSuccess, fileData = pcall(function()
                    return love.filesystem.newFileData(data, name)
                end)

                if fileDataSuccess and fileData then
                    -- Try to create SoundData for waveform visualization
                    local sdSuccess, sd = pcall(function()
                        return love.sound.newSoundData(fileData)
                    end)
                    if sdSuccess and sd then
                        soundData = sd
                    end

                    local sourceSuccess, src = pcall(function()
                        if soundData then
                            return love.audio.newSource(soundData)
                        else
                            return love.audio.newSource(fileData, "static")
                        end
                    end)

                    if sourceSuccess and src then
                        source = src
                    else
                        print("Failed to create audio source: " .. tostring(src))
                    end
                else
                    print("Failed to create file data: " .. tostring(fileData))
                end
            else
                print("File is empty or could not be read")
            end
        else
            print("Could not open file: " .. path)
        end
    end

    if source then
        local instrument = {
            name = name,
            source = source,
            soundData = soundData,  -- Store for waveform visualization
            path = path
        }
        table.insert(self.instruments, instrument)
        print("Loaded instrument: " .. name .. " (index " .. #self.instruments .. ")")
        return #self.instruments
    else
        print("Failed to load instrument: " .. path)
    end
    return nil
end

-- Play a note on a channel (only affects channel if note has content)
function Tracker:playNote(channel, note)
    if not note then
        return  -- Empty, don't affect channel
    end

    -- Only process if note has actual content
    if note.note == nil then
        return  -- Empty note slot, let existing sound continue
    end

    -- Stop note - cut audio on this channel
    if note.note == -1 then
        if self.channelSources[channel] then
            self.channelSources[channel]:stop()
            self.channelSources[channel] = nil
            self.channelInstruments[channel] = nil
        end
        return
    end

    -- Play new note - stop previous and start new
    if self.channelSources[channel] then
        self.channelSources[channel]:stop()
        self.channelSources[channel] = nil
        self.channelInstruments[channel] = nil
    end

    -- Play instrument if available
    local instIdx = note.instrument
    if instIdx and self.instruments[instIdx] and self.instruments[instIdx].source then
        local inst = self.instruments[instIdx]
        local source = inst.source:clone()

        -- Calculate pitch based on note (assuming C-4 = base pitch)
        local baseMidi = 48  -- C-4
        local semitones = note.note - baseMidi
        local pitch = 2 ^ (semitones / 12)
        source:setPitch(pitch)

        -- Apply channel volume
        local vol = self.channelVolumes[channel]
        if vol then
            local avgVol = ((vol.left or 255) + (vol.right or 255)) / 2 / 255
            source:setVolume(avgVol)
        else
            source:setVolume(1.0)
        end

        source:play()
        self.channelSources[channel] = source
        self.channelInstruments[channel] = instIdx
    else
        -- Debug: instrument not found
        if instIdx then
            print("Warning: Instrument " .. instIdx .. " not loaded")
        end
    end
end

-- Stop all channels
function Tracker:stopAllChannels()
    for ch = 1, self.channelCount do
        if self.channelSources[ch] then
            self.channelSources[ch]:stop()
            self.channelSources[ch] = nil
            self.channelInstruments[ch] = nil
        end
    end
end

-- Start playback from beginning of selected pattern
function Tracker:play()
    -- Start from selected pattern in playlist
    if PatternListComponent then
        self.currentOrderIndex = PatternListComponent.selectedIndex
    end
    self.currentRow = 0
    self.cursorRow = 0
    self.rowTimer = 0
    self.playing = true

    -- Reset command processor state
    if Commands then
        Commands:init()
    end

    print("Playback started at order " .. self.currentOrderIndex .. ", row " .. self.currentRow)
end

-- Stop playback
function Tracker:stop()
    self.playing = false
    self:stopAllChannels()
    print("Playback stopped")
end

-- Toggle playback
function Tracker:togglePlay()
    if self.playing then
        self:stop()
    else
        self:play()
    end
end

-- Get seconds per row from BPM (assuming 4 rows per beat)
function Tracker:getRowDuration()
    return 60 / self.tempo / 4
end

-- Update playback (called every frame)
function Tracker:update(dt)
    if not self.playing then
        return
    end

    -- Check for pause from commands
    if Commands and Commands:shouldPause() then
        return
    end

    self.rowTimer = self.rowTimer + dt
    local rowDuration = self:getRowDuration()

    while self.rowTimer >= rowDuration do
        self.rowTimer = self.rowTimer - rowDuration

        -- Play current row
        local patternIdx = self:getCurrentPatternIndex()

        -- First pass: play all notes
        for ch = 1, self.channelCount do
            local note = self:getNote(patternIdx, ch, self.currentRow)
            self:playNote(ch, note)
        end

        -- Second pass: execute all commands left to right, collect jump info
        local shouldJump = false
        local jumpOrder, jumpRow = nil, nil

        for ch = 1, self.channelCount do
            local note = self:getNote(patternIdx, ch, self.currentRow)

            -- Execute command if present (command can be 0 which is NOP)
            if note and note.command ~= nil and Commands then
                local jump, newOrder, newRow = Commands:execute(
                    note.command, note.operand1, note.operand2, ch, self
                )
                -- Only take the first jump encountered (left to right)
                if jump and not shouldJump then
                    shouldJump = true
                    jumpOrder = newOrder
                    jumpRow = newRow
                end
            end
        end

        -- Update visual cursor to follow playback
        self.cursorRow = self.currentRow

        -- Handle jump from commands
        if shouldJump and jumpOrder and jumpRow then
            self.currentOrderIndex = jumpOrder
            self.currentRow = jumpRow
            if PatternListComponent then
                PatternListComponent.selectedIndex = self.currentOrderIndex
                PatternListComponent.scrollToSelection()
            end
        else
            -- Get playback direction from commands
            local direction = Commands and Commands.direction or 1

            -- Advance to next row
            self.currentRow = self.currentRow + direction

            -- Handle pattern boundaries
            if self.currentRow >= self.patternLength then
                self.currentRow = 0
                self.currentOrderIndex = self.currentOrderIndex + 1
                if self.currentOrderIndex > #self.playOrder then
                    self.currentOrderIndex = 1
                end
                if PatternListComponent then
                    PatternListComponent.selectedIndex = self.currentOrderIndex
                    PatternListComponent.scrollToSelection()
                end
            elseif self.currentRow < 0 then
                self.currentRow = self.patternLength - 1
                self.currentOrderIndex = self.currentOrderIndex - 1
                if self.currentOrderIndex < 1 then
                    self.currentOrderIndex = #self.playOrder
                end
                if PatternListComponent then
                    PatternListComponent.selectedIndex = self.currentOrderIndex
                    PatternListComponent.scrollToSelection()
                end
            end
        end
    end
end

-- Format note for display
function Tracker:formatNote(note)
    if not note then
        return "--- -- -- -- --"
    end
    local noteStr = noteToString(note.note)
    local instStr = toHex(note.instrument, 2)
    local cmdStr = toHex(note.command, 2)
    local op1Str = toHex(note.operand1, 2)
    local op2Str = toHex(note.operand2, 2)
    return noteStr .. " " .. instStr .. " " .. cmdStr .. " " .. op1Str .. " " .. op2Str
end

return Tracker
