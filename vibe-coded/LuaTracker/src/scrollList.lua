-- Scrollable list component

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@param itemHeight integer
---@return scrollList
function newScrollList(x, y, w, h, itemHeight)

    ---@class scrollList
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        itemHeight = itemHeight or 20,
        items = {},
        scrollOffset = 0,
        selectedIndex = 1,
        centerSelection = true,  -- Keep selected item in center
        onSelect = nil,          -- Callback when selection changes
    }

    function self.setItems(newItems)
        self.items = newItems
        self.scrollOffset = 0
        if self.selectedIndex > #self.items then
            self.selectedIndex = math.max(1, #self.items)
        end
    end

    function self.getVisibleCount()
        return math.floor(self.h / self.itemHeight)
    end

    function self.scrollToSelection()
        if self.centerSelection then
            local visibleCount = self.getVisibleCount()
            local centerOffset = math.floor(visibleCount / 2)
            self.scrollOffset = self.selectedIndex - centerOffset - 1
        else
            -- Ensure selection is visible
            if self.selectedIndex - 1 < self.scrollOffset then
                self.scrollOffset = self.selectedIndex - 1
            elseif self.selectedIndex > self.scrollOffset + self.getVisibleCount() then
                self.scrollOffset = self.selectedIndex - self.getVisibleCount()
            end
        end
        -- Clamp scroll offset
        local maxOffset = math.max(0, #self.items - self.getVisibleCount())
        self.scrollOffset = math.max(0, math.min(self.scrollOffset, maxOffset))
    end

    function self.selectNext()
        if self.selectedIndex < #self.items then
            self.selectedIndex = self.selectedIndex + 1
            self.scrollToSelection()
            if self.onSelect then self.onSelect(self.selectedIndex) end
        end
    end

    function self.selectPrev()
        if self.selectedIndex > 1 then
            self.selectedIndex = self.selectedIndex - 1
            self.scrollToSelection()
            if self.onSelect then self.onSelect(self.selectedIndex) end
        end
    end

    function self.setSelection(index)
        self.selectedIndex = math.max(1, math.min(index, #self.items))
        self.scrollToSelection()
        if self.onSelect then self.onSelect(self.selectedIndex) end
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, true)

        love.graphics.setScissor(self.x + 1, self.y + 1, self.w - 2, self.h - 2)

        local visibleCount = self.getVisibleCount()
        local startIdx = self.scrollOffset + 1
        local endIdx = math.min(startIdx + visibleCount, #self.items)

        for i = startIdx, endIdx do
            local itemY = self.y + (i - startIdx) * self.itemHeight

            -- Selection highlight (center position)
            if i == self.selectedIndex then
                setColor(THEME.selection)
                love.graphics.rectangle("fill", self.x + 1, itemY, self.w - 2, self.itemHeight)
            end

            -- Item text
            setColor(THEME.text)
            local item = self.items[i]
            if type(item) == "table" then
                love.graphics.print(item.text or tostring(i), self.x + 4, itemY + 2)
            else
                love.graphics.print(tostring(item), self.x + 4, itemY + 2)
            end
        end

        love.graphics.setScissor()

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
    end

    function self.hovers(mx, my)
        return mx >= self.x and mx <= self.x + self.w and
               my >= self.y and my <= self.y + self.h
    end

    function self.wheelmoved(wx, wy)
        local mx, my = love.mouse.getPosition()
        if self.hovers(mx, my) then
            if wy > 0 then
                self.selectPrev()
            elseif wy < 0 then
                self.selectNext()
            end
        end
    end

    function self.mousepressed(mx, my, button, istouch)
        if button == 1 and self.hovers(mx, my) then
            local relY = my - self.y
            local clickedIdx = self.scrollOffset + math.floor(relY / self.itemHeight) + 1
            if clickedIdx >= 1 and clickedIdx <= #self.items then
                self.setSelection(clickedIdx)
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
