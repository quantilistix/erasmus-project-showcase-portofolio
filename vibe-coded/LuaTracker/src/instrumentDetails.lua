-- Instrument details panel

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return instrumentDetails
function newInstrumentDetails(x, y, w, h)

    ---@class instrumentDetails
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        previewBtn = nil,
        stopBtn = nil,
        previewSource = nil,
    }

    function self.buildButtons()
        local btnW = (self.w - 12) / 2
        local btnH = 24
        local btnY = self.y + self.h - btnH - 4

        self.previewBtn = newButton("Preview", self.x + 4, btnY, btnW, btnH, function()
            self.playPreview()
        end, true)

        self.stopBtn = newButton("Stop", self.x + 8 + btnW, btnY, btnW, btnH, function()
            self.stopPreview()
        end, true)
    end

    function self.playPreview()
        self.stopPreview()
        local inst = Tracker.instruments[Tracker.selectedInstrument]
        if inst and inst.source then
            self.previewSource = inst.source:clone()
            self.previewSource:play()
        end
    end

    function self.stopPreview()
        if self.previewSource then
            self.previewSource:stop()
            self.previewSource = nil
        end
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, true)

        -- Header
        setColor(THEME.text)
        love.graphics.print("Instrument Details", self.x + 4, self.y + 2)

        local y = self.y + 22
        local lineH = 16
        local labelX = self.x + 6
        local valueX = self.x + 60

        -- Get selected instrument
        local inst = Tracker.instruments[Tracker.selectedInstrument]

        if inst then
            -- Index
            setColor(THEME.textDim)
            love.graphics.print("Index:", labelX, y)
            setColor(THEME.text)
            love.graphics.print(toHex(Tracker.selectedInstrument, 2), valueX, y)
            y = y + lineH

            -- Name
            setColor(THEME.textDim)
            love.graphics.print("Name:", labelX, y)
            setColor(THEME.text)
            y = y + lineH

            -- Word wrap name
            local name = inst.name or "???"
            local maxChars = math.floor((self.w - 12) / 7)
            for i = 1, #name, maxChars do
                love.graphics.print(name:sub(i, i + maxChars - 1), labelX, y)
                y = y + lineH - 2
            end
            y = y + 4

            -- Path
            setColor(THEME.textDim)
            love.graphics.print("Path:", labelX, y)
            y = y + lineH

            setColor(THEME.text)
            local path = inst.path or "???"
            for i = 1, #path, maxChars do
                love.graphics.print(path:sub(i, i + maxChars - 1), labelX, y)
                y = y + lineH - 2
            end
            y = y + 8

            -- Audio info
            if inst.source then
                setColor(THEME.textDim)
                love.graphics.print("Duration:", labelX, y)
                setColor(THEME.text)
                local duration = inst.source:getDuration()
                love.graphics.print(string.format("%.2fs", duration), valueX + 10, y)
                y = y + lineH

                setColor(THEME.textDim)
                love.graphics.print("Channels:", labelX, y)
                setColor(THEME.text)
                local channels = inst.source:getChannelCount()
                love.graphics.print(tostring(channels), valueX + 10, y)
                y = y + lineH

                setColor(THEME.textDim)
                love.graphics.print("Type:", labelX, y)
                setColor(THEME.text)
                local stype = inst.source:getType()
                love.graphics.print(stype, valueX + 10, y)
                y = y + lineH
            end

            y = y + 10

            -- Usage hint
            setColor(THEME.textDim)
            love.graphics.print("Base note: C-4", labelX, y)
            y = y + lineH
            love.graphics.print("Piano keys:", labelX, y)
            y = y + lineH
            love.graphics.print("A-L = notes", labelX, y)
            y = y + lineH
            love.graphics.print("W,E,T,Y,U,O,P", labelX, y)
            y = y + lineH
            love.graphics.print("= sharps", labelX, y)

        else
            setColor(THEME.textDim)
            love.graphics.print("No instrument", labelX, y)
            y = y + lineH * 2
            love.graphics.print("Load instruments", labelX, y)
            y = y + lineH
            love.graphics.print("via menu:", labelX, y)
            y = y + lineH
            love.graphics.print("'Load Inst'", labelX, y)
            y = y + lineH * 2
            love.graphics.print("Or File >", labelX, y)
            y = y + lineH
            love.graphics.print("'Load Mod'", labelX, y)
        end

        -- Preview buttons
        if self.previewBtn then self.previewBtn.draw() end
        if self.stopBtn then self.stopBtn.draw() end

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
    end

    function self.mousepressed(mx, my, button, istouch)
        if self.previewBtn then self.previewBtn.mousepressed(mx, my, button, istouch) end
        if self.stopBtn then self.stopBtn.mousepressed(mx, my, button, istouch) end
    end

    function self.mousereleased(mx, my, button, istouch)
        if self.previewBtn then self.previewBtn.mousereleased(mx, my, button, istouch) end
        if self.stopBtn then self.stopBtn.mousereleased(mx, my, button, istouch) end
    end

    function self.setPosition(nx, ny)
        self.x = nx
        self.y = ny
        self.buildButtons()
    end

    function self.setSize(nw, nh)
        self.w = nw
        self.h = nh
        self.buildButtons()
    end

    -- Initialize
    self.buildButtons()

    return self
end
