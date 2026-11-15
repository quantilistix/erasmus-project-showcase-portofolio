-- Variables panel: octave, tempo, channels, record toggle

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return variablesPanel
function newVariablesPanel(x, y, w, h)

    ---@class variablesPanel
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        recording = false,  -- Record mode toggle
    }

    -- Variable definitions
    self.variables = {
        {
            name = "Record",
            get = function() return self.recording and "ON" or "OFF" end,
            inc = function() self.recording = true end,
            dec = function() self.recording = false end,
            toggle = function() self.recording = not self.recording end,
        },
        {
            name = "Octave",
            get = function() return tostring(Tracker.currentOctave or 4) end,
            inc = function() Tracker.currentOctave = math.min(9, (Tracker.currentOctave or 4) + 1) end,
            dec = function() Tracker.currentOctave = math.max(0, (Tracker.currentOctave or 4) - 1) end,
        },
        {
            name = "Tempo",
            get = function() return string.format("%03d", Tracker.tempo) end,
            inc = function() Tracker.tempo = math.min(999, Tracker.tempo + 5) end,
            dec = function() Tracker.tempo = math.max(1, Tracker.tempo - 5) end,
        },
        {
            name = "Channels",
            get = function() return tostring(Tracker.channelCount) end,
            inc = function() self.changeChannelCount(Tracker.channelCount + 1) end,
            dec = function() self.changeChannelCount(Tracker.channelCount - 1) end,
        },
    }

    function self.changeChannelCount(newCount)
        newCount = math.max(1, math.min(16, newCount))
        if newCount == Tracker.channelCount then return end

        local oldCount = Tracker.channelCount
        Tracker.channelCount = newCount

        -- Update channel sources
        for i = oldCount + 1, newCount do
            Tracker.channelSources[i] = nil
            Tracker.channelInstruments[i] = nil
        end

        -- Update patterns
        for _, pattern in ipairs(Tracker.patterns) do
            -- Add new channels
            for ch = oldCount + 1, newCount do
                pattern.channels[ch] = {notes = {}}
                for row = 1, Tracker.patternLength do
                    pattern.channels[ch].notes[row] = Tracker:createEmptyNote()
                end
            end
        end

        -- Clamp cursor channel
        if Tracker.cursorChannel > newCount then
            Tracker.cursorChannel = newCount
        end
    end

    function self.isRecording()
        return self.recording
    end

    function self.hovers(mx, my)
        return mx >= self.x and mx <= self.x + self.w and
               my >= self.y and my <= self.y + self.h
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, false)

        local varCount = #self.variables
        local varW = math.floor(self.w / varCount)
        local padding = 4

        for i, var in ipairs(self.variables) do
            local vx = self.x + (i - 1) * varW
            local vy = self.y + padding

            -- Variable name
            setColor(THEME.textDim)
            love.graphics.print(var.name .. ":", vx + 4, vy)

            -- Value
            setColor(THEME.text)
            local value = var.get()
            love.graphics.print(value, vx + 4, vy + 14)

            -- +/- buttons
            local btnW = 20
            local btnH = 16
            local btnY = vy + 30

            -- Minus button
            setColor(THEME.buttonNormal)
            love.graphics.rectangle("fill", vx + 4, btnY, btnW, btnH)
            setColor(THEME.border)
            love.graphics.rectangle("line", vx + 4, btnY, btnW, btnH)
            setColor(THEME.text)
            love.graphics.print("-", vx + 10, btnY + 1)

            -- Plus button
            setColor(THEME.buttonNormal)
            love.graphics.rectangle("fill", vx + 4 + btnW + 4, btnY, btnW, btnH)
            setColor(THEME.border)
            love.graphics.rectangle("line", vx + 4 + btnW + 4, btnY, btnW, btnH)
            setColor(THEME.text)
            love.graphics.print("+", vx + 10 + btnW + 4, btnY + 1)

            -- Highlight record if on
            if var.name == "Record" and self.recording then
                setColor(THEME.selectionSolid)
                love.graphics.rectangle("line", vx + 2, vy - 2, varW - 4, self.h - padding * 2)
            end
        end

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
    end

    function self.mousepressed(mx, my, button, istouch)
        if button == 1 and self.hovers(mx, my) then
            local varCount = #self.variables
            local varW = math.floor(self.w / varCount)
            local relX = mx - self.x
            local relY = my - self.y

            local varIdx = math.floor(relX / varW) + 1
            if varIdx >= 1 and varIdx <= varCount then
                local var = self.variables[varIdx]
                local btnY = 34
                local btnW = 20
                local btnH = 16
                local localX = relX - (varIdx - 1) * varW

                if relY >= btnY and relY < btnY + btnH then
                    if localX >= 4 and localX < 4 + btnW then
                        var.dec()
                    elseif localX >= 4 + btnW + 4 and localX < 4 + btnW * 2 + 4 then
                        var.inc()
                    end
                elseif var.toggle then
                    -- Click anywhere else toggles (for Record)
                    var.toggle()
                end
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
