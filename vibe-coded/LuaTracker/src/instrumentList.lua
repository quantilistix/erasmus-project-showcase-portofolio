-- Instrument list with pagination

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return instrumentList
function newInstrumentList(x, y, w, h)

    ---@class instrumentList
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        itemHeight = 18,
        currentPage = 1,
        itemsPerPage = 16,
        selectedIndex = 1,
    }

    function self.getTotalPages()
        return math.max(1, math.ceil(#Tracker.instruments / self.itemsPerPage))
    end

    function self.getPageItems()
        local items = {}
        local startIdx = (self.currentPage - 1) * self.itemsPerPage + 1
        local endIdx = math.min(startIdx + self.itemsPerPage - 1, #Tracker.instruments)
        for i = startIdx, endIdx do
            table.insert(items, {index = i, instrument = Tracker.instruments[i]})
        end
        return items
    end

    function self.nextPage()
        if self.currentPage < self.getTotalPages() then
            self.currentPage = self.currentPage + 1
        end
    end

    function self.prevPage()
        if self.currentPage > 1 then
            self.currentPage = self.currentPage - 1
        end
    end

    function self.selectInstrument(index)
        if index >= 1 and index <= #Tracker.instruments then
            self.selectedIndex = index
            Tracker.selectedInstrument = index
            -- Switch to correct page
            self.currentPage = math.ceil(index / self.itemsPerPage)
        end
    end

    function self.hovers(mx, my)
        return mx >= self.x and mx <= self.x + self.w and
               my >= self.y and my <= self.y + self.h
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, true)

        -- Header with page info
        setColor(THEME.text)
        local pageText = string.format("Instruments [%d/%d]", self.currentPage, self.getTotalPages())
        love.graphics.print(pageText, self.x + 4, self.y + 2)

        -- Page buttons
        local btnSize = 18
        setColor(THEME.buttonNormal)
        love.graphics.rectangle("fill", self.x + self.w - 40, self.y + 2, btnSize, btnSize)
        love.graphics.rectangle("fill", self.x + self.w - 20, self.y + 2, btnSize, btnSize)
        setColor(THEME.text)
        love.graphics.print("<", self.x + self.w - 36, self.y + 3)
        love.graphics.print(">", self.x + self.w - 16, self.y + 3)

        -- List
        local listY = self.y + 24
        local items = self.getPageItems()

        for i, item in ipairs(items) do
            local itemY = listY + (i - 1) * self.itemHeight

            -- Selection highlight
            if item.index == self.selectedIndex then
                setColor(THEME.selection)
                love.graphics.rectangle("fill", self.x + 1, itemY, self.w - 2, self.itemHeight)
            end

            -- Instrument text
            setColor(THEME.text)
            local text = string.format("%02X: %s", item.index, item.instrument.name or "???")
            -- Truncate if too long
            if #text > 20 then
                text = text:sub(1, 18) .. ".."
            end
            love.graphics.print(text, self.x + 4, itemY + 1)
        end

        -- Empty state
        if #Tracker.instruments == 0 then
            setColor(THEME.textDim)
            love.graphics.print("No instruments", self.x + 4, listY)
            love.graphics.print("Load via File menu", self.x + 4, listY + 16)
        end

        -- Border
        setColor(THEME.border)
        love.graphics.rectangle("line", self.x, self.y, self.w, self.h)
    end

    function self.mousepressed(mx, my, button, istouch)
        if button == 1 and self.hovers(mx, my) then
            local relX = mx - self.x
            local relY = my - self.y

            -- Page buttons
            if relY >= 2 and relY <= 20 then
                if relX >= self.w - 40 and relX < self.w - 20 then
                    self.prevPage()
                    return
                elseif relX >= self.w - 20 then
                    self.nextPage()
                    return
                end
            end

            -- List items
            if relY >= 24 then
                local itemIdx = math.floor((relY - 24) / self.itemHeight) + 1
                local items = self.getPageItems()
                if items[itemIdx] then
                    self.selectInstrument(items[itemIdx].index)
                end
            end
        end
    end

    function self.wheelmoved(wx, wy)
        local mx, my = love.mouse.getPosition()
        if self.hovers(mx, my) then
            if wy > 0 then
                self.prevPage()
            elseif wy < 0 then
                self.nextPage()
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
        self.itemsPerPage = math.floor((nh - 24) / self.itemHeight)
    end

    return self
end
