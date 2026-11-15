-- Base panel component with border

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@param usePanelBg boolean|nil
---@return panel
function newPanel(x, y, w, h, usePanelBg)

    ---@class panel
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        usePanelBg = usePanelBg or false,
        children = {}
    }

    function self.addChild(child)
        table.insert(self.children, child)
    end

    function self.draw()
        drawPanel(self.x, self.y, self.w, self.h, self.usePanelBg)

        -- Enable scissor to clip children
        love.graphics.setScissor(self.x, self.y, self.w, self.h)
        for _, child in ipairs(self.children) do
            if child.draw then child.draw() end
        end
        love.graphics.setScissor()

        -- Redraw border on top
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
    end

    function self.update(dt)
        for _, child in ipairs(self.children) do
            if child.update then child.update(dt) end
        end
    end

    function self.mousepressed(mx, my, button, istouch)
        for _, child in ipairs(self.children) do
            if child.mousepressed then child.mousepressed(mx, my, button, istouch) end
        end
    end

    function self.mousereleased(mx, my, button, istouch)
        for _, child in ipairs(self.children) do
            if child.mousereleased then child.mousereleased(mx, my, button, istouch) end
        end
    end

    function self.wheelmoved(wx, wy)
        for _, child in ipairs(self.children) do
            if child.wheelmoved then child.wheelmoved(wx, wy) end
        end
    end

    function self.keypressed(key, scancode, isRepeat)
        for _, child in ipairs(self.children) do
            if child.keypressed then child.keypressed(key, scancode, isRepeat) end
        end
    end

    function self.textinput(t)
        for _, child in ipairs(self.children) do
            if child.textinput then child.textinput(t) end
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

    function self.hovers(mx, my)
        return mx >= self.x and mx <= self.x + self.w and
               my >= self.y and my <= self.y + self.h
    end

    return self
end
