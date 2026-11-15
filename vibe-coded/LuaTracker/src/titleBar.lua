-- Title bar with app name, time/date, fullscreen, quit

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return titleBar
function newTitleBar(x, y, w, h)

    ---@class titleBar
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        fullscreenBtn = nil,
        quitBtn = nil,
    }

    function self.buildButtons()
        local btnW = 80
        local btnH = self.h - 4

        self.fullscreenBtn = newButton("Fullscreen", self.x + self.w - 170, self.y + 2, btnW, btnH, function()
            love.window.setFullscreen(not love.window.getFullscreen())
        end, true)

        self.quitBtn = newButton("Quit", self.x + self.w - 85, self.y + 2, 75, btnH, function()
            love.event.quit()
        end, true)
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, false)

        -- App title
        setColor(THEME.text)
        love.graphics.print("LuaTracker pro (test)", self.x + 8, self.y + (self.h - 14) / 2, 0, 1.2, 1.2)

        -- Time/Date in center
        local timeStr = os.date("%H:%M:%S  %Y-%m-%d")
        local timeWidth = love.graphics.getFont():getWidth(timeStr)
        love.graphics.print(timeStr, self.x + (self.w - timeWidth) / 2, self.y + (self.h - 12) / 2)

        -- Buttons
        if self.fullscreenBtn then self.fullscreenBtn.draw() end
        if self.quitBtn then self.quitBtn.draw() end
    end

    function self.mousepressed(mx, my, button, istouch)
        if self.fullscreenBtn then self.fullscreenBtn.mousepressed(mx, my, button, istouch) end
        if self.quitBtn then self.quitBtn.mousepressed(mx, my, button, istouch) end
    end

    function self.mousereleased(mx, my, button, istouch)
        if self.fullscreenBtn then self.fullscreenBtn.mousereleased(mx, my, button, istouch) end
        if self.quitBtn then self.quitBtn.mousereleased(mx, my, button, istouch) end
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
