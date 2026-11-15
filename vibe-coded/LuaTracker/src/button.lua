-- Button component with theme support

---@param text string
---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@param func function
---@param small boolean|nil
---@return button
function newButton(text, x, y, w, h, func, small)

    ---@class button
    local self = {
        text = text,
        x = x,
        y = y,
        w = w,
        h = h,
        func = func,
        pressed = false,
        small = small or false,
        enabled = true
    }

    ---@param nx integer
    ---@param ny integer
    ---@return boolean
    function self.hovers(nx, ny)
        return nx >= self.x and nx <= self.x + self.w and
               ny >= self.y and ny <= self.y + self.h
    end

    function self.mousepressed(mx, my, button, istouch)
        if button == 1 and self.enabled then
            self.pressed = self.hovers(mx, my)
        end
    end

    function self.mousereleased(mx, my, button, istouch)
        if button == 1 and self.pressed then
            self.pressed = false
            if self.hovers(mx, my) and self.enabled then
                self.func()
            end
        end
    end

    function self.draw()
        local mouseX, mouseY = love.mouse.getPosition()
        local onButton = self.hovers(mouseX, mouseY) and self.enabled

        -- Background
        if self.pressed and onButton then
            setColor(THEME.buttonPressed)
        elseif onButton then
            setColor(THEME.buttonHover)
        else
            setColor(THEME.buttonNormal)
        end
        love.graphics.rectangle("fill", self.x, self.y, self.w, self.h)

        -- Border
        if self.pressed and onButton then
            setColor(THEME.buttonPressed)
        else
            setColor(THEME.border)
        end
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)

        -- Text
        if self.enabled then
            setColor(THEME.text)
        else
            setColor(THEME.textDim)
        end

        -- Calculate scale to fit text in button
        local font = love.graphics.getFont()
        local textW = font:getWidth(self.text)
        local textH = font:getHeight()

        -- Scale to fit with padding
        local maxW = self.w - 6
        local maxH = self.h - 4
        local scaleX = maxW / textW
        local scaleY = maxH / textH
        local scale = math.min(scaleX, scaleY, 2)  -- Cap at 2x

        local scaledW = textW * scale
        local scaledH = textH * scale
        local tx = self.x + (self.w - scaledW) / 2
        local ty = self.y + (self.h - scaledH) / 2
        love.graphics.print(self.text, tx, ty, 0, scale, scale)
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
