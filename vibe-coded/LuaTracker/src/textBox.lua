

---@param text string
---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return textBox
function newTextBox(text, x, y, w, h, size)

    ---@class textBox
    local self = 
    {
        text = text,
        x = x,
        y = y,
        w = w,
        h = h,
        size = size
    }

    
    function self.draw()
        love.graphics.setColor(0.25, 0.25, 0.25, 1.0)
        love.graphics.rectangle("fill", self.x, self.y, self.w, self.h)
        love.graphics.setColor(1.0, 1.0, 1.0, 1.0)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
        love.graphics.print(self.text, self.x+self.size*5/2, self.y+self.size*5/2, 0, self.size, self.size)
    end


    return self

end
