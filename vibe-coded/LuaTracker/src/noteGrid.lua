-- Main tracker note grid

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return noteGrid
function newNoteGrid(x, y, w, h)

    ---@class noteGrid
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        charWidth = 8,
        rowHeight = 16,
        rowNumWidth = 32,
        channelWidth = 120,
        focused = true,
        inputBuffer = "",
    }

    -- Field positions within a channel (character offsets)
    -- Format: "C-4 01 00 00 00"
    --          123 45 67 89 01
    self.fields = {
        {name = "note", start = 0, len = 3, editable = true, type = "note"},
        {name = "inst", start = 4, len = 2, editable = false, type = "hex"},  -- Not directly editable
        {name = "cmd",  start = 7, len = 2, editable = true, type = "hex"},
        {name = "op1",  start = 10, len = 2, editable = true, type = "hex"},
        {name = "op2",  start = 13, len = 2, editable = true, type = "hex"},
    }

    function self.getVisibleRows()
        return math.floor((self.h - 4) / self.rowHeight)
    end

    function self.getCenterRowOffset()
        return math.floor(self.getVisibleRows() / 2)
    end

    function self.getPatternIndex()
        local orderIdx = PatternListComponent and PatternListComponent.selectedIndex or 1
        return Tracker.playOrder[orderIdx] or 0
    end

    function self.getCurrentField()
        return self.fields[Tracker.cursorField] or self.fields[1]
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, true)

        local patternIdx = self.getPatternIndex()
        local visibleRows = self.getVisibleRows()
        local centerOffset = self.getCenterRowOffset()

        -- Calculate start row based on cursor position (cursor in center)
        local startRow = Tracker.cursorRow - centerOffset

        love.graphics.setScissor(self.x + 1, self.y + 1, self.w - 2, self.h - 2)

        for i = 0, visibleRows - 1 do
            local row = startRow + i
            local drawY = self.y + 2 + i * self.rowHeight

            -- Only draw valid rows
            if row >= 0 and row < Tracker.patternLength then
                local isSelected = (row == Tracker.cursorRow)

                -- Selection bar
                if isSelected then
                    setColor(THEME.selection)
                    love.graphics.rectangle("fill", self.x + 1, drawY, self.w - 2, self.rowHeight)

                    -- Draw arrows for selected row
                    setColor(THEME.selectionSolid)
                    love.graphics.print(">", self.x + 4, drawY)
                    love.graphics.print("<", self.x + self.w - 12, drawY)
                end

                -- Row number
                setColor(isSelected and THEME.selectionSolid or THEME.text)
                love.graphics.print(toHex(row, 2), self.x + 14, drawY)

                -- Draw separator
                setColor(THEME.border)
                love.graphics.line(self.x + self.rowNumWidth, drawY,
                                   self.x + self.rowNumWidth, drawY + self.rowHeight)

                -- Draw each channel
                for ch = 1, Tracker.channelCount do
                    local channelX = self.x + self.rowNumWidth + (ch - 1) * self.channelWidth
                    local note = Tracker:getNote(patternIdx, ch, row)

                    -- Format note display
                    local noteStr = noteToString(note and note.note or nil)
                    local instStr = toHex(note and note.instrument or nil, 2)
                    local cmdStr = toHex(note and note.command or nil, 2)
                    local op1Str = toHex(note and note.operand1 or nil, 2)
                    local op2Str = toHex(note and note.operand2 or nil, 2)

                    -- Print each character individually for alignment
                    local chars = noteStr .. " " .. instStr .. " " .. cmdStr .. " " .. op1Str .. " " .. op2Str
                    local charX = channelX + 4

                    for ci = 1, #chars do
                        local char = chars:sub(ci, ci)

                        -- Highlight current field if this is cursor position
                        if isSelected and ch == Tracker.cursorChannel then
                            local field = self.getCurrentField()
                            local fieldStart = field.start + 1
                            local fieldEnd = field.start + field.len

                            if ci >= fieldStart and ci <= fieldEnd and field.editable then
                                setColor(THEME.selectionSolid)
                            else
                                setColor(THEME.text)
                            end
                        else
                            setColor(THEME.text)
                        end

                        love.graphics.print(char, charX, drawY)
                        charX = charX + self.charWidth
                    end

                    -- Channel separator
                    setColor(THEME.border)
                    love.graphics.line(channelX + self.channelWidth - 1, drawY,
                                       channelX + self.channelWidth - 1, drawY + self.rowHeight)
                end
            end
        end

        love.graphics.setScissor()

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
    end

    function self.moveCursorUp()
        Tracker.cursorRow = Tracker.cursorRow - 1
        if Tracker.cursorRow < 0 then
            Tracker.cursorRow = Tracker.patternLength - 1  -- Loop to end
        end
    end

    function self.moveCursorDown()
        Tracker.cursorRow = Tracker.cursorRow + 1
        if Tracker.cursorRow >= Tracker.patternLength then
            Tracker.cursorRow = 0  -- Loop to start
        end
    end

    function self.moveCursorLeft()
        local field = self.getCurrentField()
        if Tracker.cursorField > 1 then
            -- Skip instrument field (not editable)
            Tracker.cursorField = Tracker.cursorField - 1
            if Tracker.cursorField == 2 then  -- Instrument field
                Tracker.cursorField = 1
            end
        else
            -- Move to previous channel
            if Tracker.cursorChannel > 1 then
                Tracker.cursorChannel = Tracker.cursorChannel - 1
                Tracker.cursorField = 5  -- Last field
            end
        end
    end

    function self.moveCursorRight()
        if Tracker.cursorField < 5 then
            -- Skip instrument field (not editable)
            Tracker.cursorField = Tracker.cursorField + 1
            if Tracker.cursorField == 2 then  -- Instrument field
                Tracker.cursorField = 3
            end
        else
            -- Move to next channel
            if Tracker.cursorChannel < Tracker.channelCount then
                Tracker.cursorChannel = Tracker.cursorChannel + 1
                Tracker.cursorField = 1
            end
        end
    end

    function self.jumpToNextChannel()
        if Tracker.cursorChannel < Tracker.channelCount then
            Tracker.cursorChannel = Tracker.cursorChannel + 1
        else
            Tracker.cursorChannel = 1
        end
    end

    function self.deleteNote()
        local patternIdx = self.getPatternIndex()
        local note = Tracker:getNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow)
        if note then
            local field = self.getCurrentField()
            if field.name == "note" then
                note.note = nil
                note.instrument = nil
            elseif field.name == "cmd" then
                note.command = nil
            elseif field.name == "op1" then
                note.operand1 = nil
            elseif field.name == "op2" then
                note.operand2 = nil
            end
        end
    end

    function self.insertStopNote()
        local patternIdx = self.getPatternIndex()
        local note = Tracker:getNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow)
        if not note then
            note = Tracker:createEmptyNote()
            Tracker:setNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow, note)
        end
        note.note = -1
        note.instrument = nil
        self.moveCursorDown()
    end

    function self.handleNoteInput(key)
        -- Piano layout: AWSEDFTGYHUJ (chromatic from C)
        local noteMap = {
            a = 0,   -- C
            w = 1,   -- C#
            s = 2,   -- D
            e = 3,   -- D#
            d = 4,   -- E
            f = 5,   -- F
            t = 6,   -- F#
            g = 7,   -- G
            y = 8,   -- G#
            h = 9,   -- A
            u = 10,  -- A#
            j = 11,  -- B
        }

        local midiOffset = noteMap[key:lower()]
        if midiOffset then
            local octave = Tracker.currentOctave or 4
            local midiNote = octave * 12 + midiOffset

            -- Create note for preview
            local previewNote = {
                note = midiNote,
                instrument = Tracker.selectedInstrument,
                command = nil,
                operand1 = nil,
                operand2 = nil
            }

            -- Play preview
            Tracker:playNote(Tracker.cursorChannel, previewNote)

            -- Only record if recording mode is on
            if VariablesPanelComponent and VariablesPanelComponent.isRecording() then
                local patternIdx = self.getPatternIndex()
                local note = Tracker:getNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow)
                if not note then
                    note = Tracker:createEmptyNote()
                    Tracker:setNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow, note)
                end
                note.note = midiNote
                note.instrument = Tracker.selectedInstrument

                self.moveCursorDown()
            end
            return true
        end
        return false
    end

    function self.handleHexInput(key)
        local hexMap = {
            ["0"] = 0, ["1"] = 1, ["2"] = 2, ["3"] = 3,
            ["4"] = 4, ["5"] = 5, ["6"] = 6, ["7"] = 7,
            ["8"] = 8, ["9"] = 9, ["a"] = 10, ["b"] = 11,
            ["c"] = 12, ["d"] = 13, ["e"] = 14, ["f"] = 15
        }

        local value = hexMap[key:lower()]
        if value then
            local patternIdx = self.getPatternIndex()
            local note = Tracker:getNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow)
            if not note then
                note = Tracker:createEmptyNote()
                Tracker:setNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow, note)
            end

            local field = self.getCurrentField()
            if field.name == "cmd" then
                note.command = (note.command or 0) * 16 + value
                note.command = note.command % 256
            elseif field.name == "op1" then
                note.operand1 = (note.operand1 or 0) * 16 + value
                note.operand1 = note.operand1 % 256
            elseif field.name == "op2" then
                note.operand2 = (note.operand2 or 0) * 16 + value
                note.operand2 = note.operand2 % 256
            end
            return true
        end
        return false
    end

    function self.clearCurrentCell()
        local patternIdx = self.getPatternIndex()
        local note = Tracker:getNote(patternIdx, Tracker.cursorChannel, Tracker.cursorRow)
        if note then
            local field = self.getCurrentField()
            if field.name == "note" then
                note.note = nil
                note.instrument = nil
            elseif field.name == "cmd" then
                note.command = nil
            elseif field.name == "op1" then
                note.operand1 = nil
            elseif field.name == "op2" then
                note.operand2 = nil
            end
        end
    end

    function self.keypressed(key, scancode, isRepeat)
        if not self.focused then return end

        if key == "up" then
            self.moveCursorUp()
        elseif key == "down" then
            self.moveCursorDown()
        elseif key == "left" then
            self.moveCursorLeft()
        elseif key == "right" then
            self.moveCursorRight()
        elseif key == "tab" then
            self.jumpToNextChannel()
        elseif key == "backspace" then
            -- Clear current cell
            self.clearCurrentCell()
        elseif key == "delete" then
            -- Clear and move to next note
            self.clearCurrentCell()
            self.moveCursorDown()
        elseif key == "x" then
            -- Stop note
            if VariablesPanelComponent and VariablesPanelComponent.isRecording() then
                self.insertStopNote()
            end
        elseif key == "space" then
            Tracker:togglePlay()
        else
            -- Handle input based on current field
            local field = self.getCurrentField()
            if field.type == "note" then
                if not self.handleNoteInput(key) then
                    self.handleHexInput(key)
                end
            else
                self.handleHexInput(key)
            end
        end
    end

    function self.hovers(mx, my)
        return mx >= self.x and mx <= self.x + self.w and
               my >= self.y and my <= self.y + self.h
    end

    function self.mousepressed(mx, my, button, istouch)
        if button == 1 and self.hovers(mx, my) then
            self.focused = true

            local relX = mx - self.x
            local relY = my - self.y

            -- Calculate clicked row
            local visibleRows = self.getVisibleRows()
            local centerOffset = self.getCenterRowOffset()
            local startRow = Tracker.cursorRow - centerOffset

            local clickedRowOffset = math.floor(relY / self.rowHeight)
            local clickedRow = startRow + clickedRowOffset

            if clickedRow >= 0 and clickedRow < Tracker.patternLength then
                Tracker.cursorRow = clickedRow
            end

            -- Calculate clicked channel
            local channelAreaX = relX - self.rowNumWidth
            if channelAreaX >= 0 then
                local clickedChannel = math.floor(channelAreaX / self.channelWidth) + 1
                if clickedChannel >= 1 and clickedChannel <= Tracker.channelCount then
                    Tracker.cursorChannel = clickedChannel

                    -- Calculate clicked field
                    local fieldX = channelAreaX % self.channelWidth
                    local charPos = math.floor(fieldX / self.charWidth)

                    for i, field in ipairs(self.fields) do
                        if charPos >= field.start and charPos < field.start + field.len then
                            if field.editable then
                                Tracker.cursorField = i
                            end
                            break
                        end
                    end
                end
            end
        end
    end

    function self.wheelmoved(wx, wy)
        local mx, my = love.mouse.getPosition()
        if self.hovers(mx, my) then
            if wy > 0 then
                self.moveCursorUp()
            elseif wy < 0 then
                self.moveCursorDown()
            end
        end
    end

    function self.setPosition(nx, ny)
        self.x = nx
        self.y = ny
    end

    function self.setSize(nw, nh)
        self.w = nw
        self.h = nh
    end

    return self
end
