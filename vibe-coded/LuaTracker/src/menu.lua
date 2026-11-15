
---@param title string
---@param objects table
---@return menu
function newMenu(title, objects)
    ---@class menu
    local self =
    {
        title = title,
        objects = objects
    }


    function self.draw()
        local width, height = love.graphics.getWidth(), love.graphics.getHeight()
        love.graphics.setColor(0.25, 0.25, 0.25, 1.0)
        love.graphics.rectangle("fill", 0, 0, width, height)
        love.graphics.setColor(1.0, 1.0, 1.0, 1.0)
        love.graphics.rectangle("line", 0, 0, width, 42)
        love.graphics.print(self.title, 4, 4, 0, 2, 2)
        for key, object in pairs(objects) do
            if object.draw then object.draw() end
        end
    end


    function self.update(dt)
        for key, object in pairs(objects) do
            if object.update then object.update(dt) end
        end
    end


    function self.mousepressed(x, y, button, isTouch)
        for key, object in pairs(objects) do
            if object.mousepressed then object.mousepressed(x, y, button, isTouch) end
        end
    end


    function self.mousereleased(x, y, button, isTouch)
        for key, object in pairs(objects) do
            if object.mousereleased then object.mousereleased(x, y, button, isTouch) end
        end
    end


    function self.keypressed(key, scancode, isRepeat)
        for k, object in pairs(objects) do
            if object.keypressed then object.keypressed(key, scancode, isRepeat) end
        end
    end


    function self.keyreleased(key, scancode, isRepeat)
        for k, object in pairs(objects) do
            if object.keyreleased then object.keyreleased(key, scancode, isRepeat) end
        end
    end


    function self.textinput(t)
        for k, object in pairs(objects) do
            if object.textinput then object.textinput(t) end
        end
    end


    function self.setMenu()
        love.draw = self.draw
        love.update = self.update
        love.mousepressed = self.mousepressed
        love.mousereleased = self.mousereleased
        love.keypressed = self.keypressed
        love.keyreleased = self.keyreleased
        love.textinput = self.textinput
    end

    return self
end