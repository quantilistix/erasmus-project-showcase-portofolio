-- Pattern playlist component (play order display)
-- Buttons: Up, Down, Add, Remove, Increase (pattern index), Decrease (pattern index)

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return patternList
function newPatternList(x, y, w, h)

    ---@class patternList
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        itemHeight = 20,
        scrollOffset = 0,
        selectedIndex = 1,
    }

    function self.getVisibleCount()
        local listHeight = self.h - 70  -- Space for header and buttons
        return math.floor(listHeight / self.itemHeight)
    end

    function self.scrollToSelection()
        local visibleCount = self.getVisibleCount()
        local centerOffset = math.floor(visibleCount / 2)
        self.scrollOffset = self.selectedIndex - centerOffset - 1
        local maxOffset = math.max(0, #Tracker.playOrder - visibleCount)
        self.scrollOffset = math.max(0, math.min(self.scrollOffset, maxOffset))
    end

    -- Navigate selection up
    function self.moveUp()
        if self.selectedIndex > 1 then
            self.selectedIndex = self.selectedIndex - 1
            self.scrollToSelection()
        end
    end

    -- Navigate selection down
    function self.moveDown()
        if self.selectedIndex < #Tracker.playOrder then
            self.selectedIndex = self.selectedIndex + 1
            self.scrollToSelection()
        end
    end

    -- Add new entry to playlist (creates new pattern)
    function self.addPattern()
        local newIdx = Tracker:addPattern()
        table.insert(Tracker.playOrder, self.selectedIndex + 1, newIdx)
        self.selectedIndex = self.selectedIndex + 1
        self.scrollToSelection()
    end

    -- Remove entry from playlist
    function self.removePattern()
        if #Tracker.playOrder > 1 then
            table.remove(Tracker.playOrder, self.selectedIndex)
            if self.selectedIndex > #Tracker.playOrder then
                self.selectedIndex = #Tracker.playOrder
            end
            self.scrollToSelection()
        end
    end

    -- Increase pattern index of selected entry
    function self.increasePatternIndex()
        local currentIdx = Tracker.playOrder[self.selectedIndex]
        local maxIdx = #Tracker.patterns - 1
        if currentIdx < maxIdx then
            Tracker.playOrder[self.selectedIndex] = currentIdx + 1
        end
    end

    -- Decrease pattern index of selected entry
    function self.decreasePatternIndex()
        local currentIdx = Tracker.playOrder[self.selectedIndex]
        if currentIdx > 0 then
            Tracker.playOrder[self.selectedIndex] = currentIdx - 1
        end
    end

    function self.hovers(mx, my)
        return mx >= self.x and mx <= self.x + self.w and
               my >= self.y and my <= self.y + self.h
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, true)

        -- Header
        setColor(THEME.text)
        love.graphics.print("Play Order", self.x + 4, self.y + 2)

        -- Control buttons (2 rows of 3)
        local btnStartY = self.y + 18
        local btnW = math.floor((self.w - 8) / 3)
        local btnH = 18

        local buttons = {
            {label = "Up", x = 0, y = 0, action = self.moveUp},
            {label = "Down", x = 1, y = 0, action = self.moveDown},
            {label = "+", x = 2, y = 0, action = self.addPattern},
            {label = "-", x = 0, y = 1, action = self.removePattern},
            {label = "<<", x = 1, y = 1, action = self.decreasePatternIndex},
            {label = ">>", x = 2, y = 1, action = self.increasePatternIndex},
        }

        for _, btn in ipairs(buttons) do
            local bx = self.x + 2 + btn.x * (btnW + 2)
            local by = btnStartY + btn.y * (btnH + 2)

            setColor(THEME.buttonNormal)
            love.graphics.rectangle("fill", bx, by, btnW, btnH)
            setColor(THEME.border)
            love.graphics.rectangle("line", bx, by, btnW, btnH)
            setColor(THEME.text)

            local font = love.graphics.getFont()
            local textW = font:getWidth(btn.label)
            love.graphics.print(btn.label, bx + (btnW - textW) / 2, by + 2)
        end

        -- List area
        local listStartY = btnStartY + 2 * (btnH + 2) + 4
        local listHeight = self.h - (listStartY - self.y) - 4

        love.graphics.setScissor(self.x + 1, listStartY, self.w - 2, listHeight)

        local visibleCount = math.floor(listHeight / self.itemHeight)
        local startIdx = self.scrollOffset + 1
        local endIdx = math.min(startIdx + visibleCount, #Tracker.playOrder)

        for i = startIdx, endIdx do
            local itemY = listStartY + (i - startIdx) * self.itemHeight

            -- Selection highlight
            if i == self.selectedIndex then
                setColor(THEME.selection)
                love.graphics.rectangle("fill", self.x + 1, itemY, self.w - 2, self.itemHeight)
            end

            -- Format: "order: patternIdx"
            setColor(THEME.text)
            local patternIdx = Tracker.playOrder[i]
            local text = string.format("%02X: Pat %02X", i - 1, patternIdx)
            love.graphics.print(text, self.x + 6, itemY + 2)
        end

        love.graphics.setScissor()

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
    end

    function self.wheelmoved(wx, wy)
        local mx, my = love.mouse.getPosition()
        if self.hovers(mx, my) then
            if wy > 0 then
                self.moveUp()
            elseif wy < 0 then
                self.moveDown()
            end
        end
    end

    function self.mousepressed(mx, my, button, istouch)
        if button == 1 and self.hovers(mx, my) then
            local relX = mx - self.x
            local relY = my - self.y

            local btnStartY = 18
            local btnW = math.floor((self.w - 8) / 3)
            local btnH = 18

            -- Check button clicks
            local buttons = {
                {x = 0, y = 0, action = self.moveUp},
                {x = 1, y = 0, action = self.moveDown},
                {x = 2, y = 0, action = self.addPattern},
                {x = 0, y = 1, action = self.removePattern},
                {x = 1, y = 1, action = self.decreasePatternIndex},
                {x = 2, y = 1, action = self.increasePatternIndex},
            }

            for _, btn in ipairs(buttons) do
                local bx = 2 + btn.x * (btnW + 2)
                local by = btnStartY + btn.y * (btnH + 2)
                if relX >= bx and relX < bx + btnW and relY >= by and relY < by + btnH then
                    btn.action()
                    return
                end
            end

            -- Check list clicks
            local listStartY = btnStartY + 2 * (btnH + 2) + 4
            if relY >= listStartY then
                local listY = relY - listStartY
                local clickedIdx = self.scrollOffset + math.floor(listY / self.itemHeight) + 1
                if clickedIdx >= 1 and clickedIdx <= #Tracker.playOrder then
                    self.selectedIndex = clickedIdx
                    self.scrollToSelection()
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
