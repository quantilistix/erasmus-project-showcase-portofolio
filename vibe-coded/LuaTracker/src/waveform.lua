-- Waveform visualization for channels

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@param channelCount integer
---@return waveformDisplay
function newWaveformDisplay(x, y, w, h, channelCount)

    ---@class waveformDisplay
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        channelCount = channelCount or 4,
    }

    function self.getChannelBox(channel)
        -- Arrange channels horizontally when wide, or in grid when narrow
        local availW = self.w - 4
        local availH = self.h - 24

        local cols, rows
        if availW > availH * 2 then
            -- Wide layout: all channels in a row
            cols = self.channelCount
            rows = 1
        else
            -- Compact layout: 2x2 grid
            cols = 2
            rows = math.ceil(self.channelCount / cols)
        end

        local boxW = availW / cols
        local boxH = availH / rows

        local col = (channel - 1) % cols
        local row = math.floor((channel - 1) / cols)

        return {
            x = self.x + 2 + col * boxW,
            y = self.y + 22 + row * boxH,
            w = boxW - 2,
            h = boxH - 2
        }
    end

    function self.drawChannelWaveform(channel, box)
        -- Background
        setColor(THEME.panelBg)
        love.graphics.rectangle("fill", box.x, box.y, box.w, box.h)

        local centerY = box.y + box.h / 2
        local amplitude = (box.h / 2) - 4

        -- Get current playing source and instrument for this channel
        local source = Tracker.channelSources[channel]
        local instIdx = Tracker.channelInstruments[channel]

        if source and source:isPlaying() and instIdx then
            local inst = Tracker.instruments[instIdx]
            local soundData = inst and inst.soundData

            if soundData then
                -- Draw actual waveform from sound data
                local sampleCount = soundData:getSampleCount()
                local channels = soundData:getChannelCount()
                local sampleRate = soundData:getSampleRate()

                -- Get current playback position
                local position = source:tell("samples")
                local pitch = source:getPitch()

                -- Calculate visible window (show ~50ms of audio centered on position)
                local windowSamples = math.floor(sampleRate * 0.05)
                local startSample = math.max(0, position - windowSamples / 2)
                local endSample = math.min(sampleCount - 1, position + windowSamples / 2)
                local visibleSamples = endSample - startSample

                if visibleSamples > 0 then
                    setColor(THEME.selectionSolid)

                    local points = {}
                    local step = math.max(1, math.floor(visibleSamples / box.w))

                    for i = 0, box.w - 1 do
                        local sampleIdx = math.floor(startSample + (i / box.w) * visibleSamples)
                        if sampleIdx >= 0 and sampleIdx < sampleCount then
                            local sample = soundData:getSample(sampleIdx, 1)  -- Get first channel
                            local px = box.x + i
                            local py = centerY - sample * amplitude
                            table.insert(points, px)
                            table.insert(points, py)
                        end
                    end

                    if #points >= 4 then
                        love.graphics.line(points)
                    end

                    -- Draw playhead position indicator
                    local playheadX = box.x + box.w / 2
                    setColor(THEME.text)
                    love.graphics.line(playheadX, box.y + 2, playheadX, box.y + box.h - 2)
                end
            else
                -- No sound data available, draw simple amplitude indicator
                setColor(THEME.selectionSolid)
                local time = love.timer.getTime() * 10
                local points = {}
                for i = 0, box.w - 1 do
                    local phase = (i / box.w) * math.pi * 8 + time
                    local py = centerY + math.sin(phase) * amplitude * 0.5
                    table.insert(points, box.x + i)
                    table.insert(points, py)
                end
                if #points >= 4 then
                    love.graphics.line(points)
                end
            end
        else
            -- Draw flat line when not playing
            setColor(THEME.textDim)
            love.graphics.line(box.x + 4, centerY, box.x + box.w - 4, centerY)
        end

        -- Channel label
        setColor(THEME.text)
        love.graphics.print("Ch" .. channel, box.x + 2, box.y + 2)

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", box.x, box.y, box.w, box.h)
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, true)

        -- Header
        setColor(THEME.text)
        love.graphics.print("Waveforms", self.x + 4, self.y + 2)

        -- Draw each channel
        for ch = 1, self.channelCount do
            local box = self.getChannelBox(ch)
            self.drawChannelWaveform(ch, box)
        end

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
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
