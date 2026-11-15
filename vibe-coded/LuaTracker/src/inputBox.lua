

---@param text string
---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@param value string
---@param cursorShape "_"|"|"
---@return inputBox
function newInputBox(text, value, x, y, w, h, cursorShape)

    ---@class inputBox
    local self = 
    {
        text = text,
        x = x,
        y = y,
        w = w,
        h = h,
        value = value,
        old_value = value,
        pressed = false,
        active = false,
        cursor = false,
        timer = 0,
        shape = cursorShape
    }

    ---@param x integer
    ---@param y integer
    ---@return boolean
    function self.hovers(x, y)
        if  x >= self.x and x <= self.x + self.w and
            y >= self.y and y <= self.y + self.h then
            return true
        else
            return false
        end
    end

    ---@param x integer
    ---@param y integer
    ---@param button integer
    ---@param istouch boolean
    function self.mousepressed(x, y, button, istouch)
        if button == 1 then
            self.pressed = self.hovers(x, y)
            if not self.pressed then self.active = false; self.old_value = self.value end
        end
    end

    ---@param x integer
    ---@param y integer
    ---@param button integer
    ---@param istouch boolean
    function self.mousereleased(x, y, button, istouch)
        if button == 1 and self.pressed then
            self.pressed = false
            self.active = self.hovers(x, y)
            if self.active == false then self.old_value = self.value end
        end
    end
    
    function self.draw()

        local mouseX, mouseY = love.mouse.getX(), love.mouse.getY()
        local onButton = self.hovers(mouseX, mouseY)
        local cursor = ""
        if self.cursor and self.active then cursor = self.shape end
        
        if self.pressed and onButton then
            love.graphics.setColor(0.125, 0.125, 0.125, 1.0)
        else
            if onButton then
                love.graphics.setColor(0.25, 0.25, 0.25, 1.0)
            else
                love.graphics.setColor(0.125, 0.125, 0.125, 1.0)
            end
        end
        love.graphics.rectangle("fill", self.x, self.y, self.w, self.h)
        if self.pressed and onButton then
            love.graphics.setColor(0.5, 0.5, 0.5, 1.0)
        else
            if onButton then
                love.graphics.setColor(1.0, 1.0, 1.0, 1.0)
            else
                love.graphics.setColor(1.0, 1.0, 1.0, 1.0)
            end
        end
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
        if self.value == "" and not self.active then
            if self.pressed and onButton then
                love.graphics.setColor(0.5, 0.5, 0.5, 0.5)
            else
                if onButton then
                    love.graphics.setColor(1.0, 1.0, 1.0, 0.5)
                else
                    love.graphics.setColor(1.0, 1.0, 1.0, 0.5)
                end
            end
            love.graphics.print(self.text, self.x+self.h/8, self.y+self.h/8, 0, self.h/20, self.h/20)
        else
            if self.value:len() > 10 then
                if self.active then
                    love.graphics.print("..."..self.value:sub(self.value:len()-8)..cursor, self.x+self.h/8, self.y+self.h/8, 0, self.h/20, self.h/20)
                else
                    love.graphics.print(self.value:sub(1, 8).."..."..cursor, self.x+self.h/8, self.y+self.h/8, 0, self.h/20, self.h/20)
                end
            else
                love.graphics.print(self.value..cursor, self.x+self.h/8, self.y+self.h/8, 0, self.h/20, self.h/20)
            end
        end
    end


    function self.update(dt)
        self.timer = self.timer + dt
        if self.timer > 0.25 then self.timer = self.timer - 0.25; self.cursor = not self.cursor end
    end


    function self.textinput(t)
        if self.active then
            self.value = self.value .. t
        end
    end

    function self.keypressed(key, scancode, isRepeat)
        if self.active then
            if      key == "return"     then self.active = false; self.old_value = self.value
            elseif  key == "escape"     then self.active = false; self.value = self.old_value
            elseif  key == "backspace"  then self.value = self.value:sub(1, self.value:len()-1)
            end
        end
    end


    return self

end
