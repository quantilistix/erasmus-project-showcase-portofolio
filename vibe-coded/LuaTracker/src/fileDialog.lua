-- Simple file dialog for loading/saving
-- Uses keyboard input for path entry

---@param mode string "save" or "load"
---@param title string
---@param extension string
---@param callback function
---@return fileDialog
function newFileDialog(mode, title, extension, callback)

    ---@class fileDialog
    local self = {
        mode = mode,
        title = title,
        extension = extension,
        callback = callback,
        path = "./samples/",
        cursorVisible = true,
        cursorTimer = 0,
        active = true,
        message = "",
    }

    function self.draw()
        local w, h = love.graphics.getDimensions()
        local dialogW, dialogH = 500, 200
        local x = (w - dialogW) / 2
        local y = (h - dialogH) / 2

        -- Overlay
        love.graphics.setColor(0, 0, 0, 0.7)
        love.graphics.rectangle("fill", 0, 0, w, h)

        -- Dialog box
        drawPanel(x, y, dialogW, dialogH, true)

        -- Title
        setColor(THEME.text)
        love.graphics.print(self.title, x + 10, y + 10, 0, 1.5, 1.5)

        -- Instructions
        if self.extension ~= "" then
            love.graphics.print("Enter file path (without extension):", x + 10, y + 45)
        else
            love.graphics.print("Enter full file path:", x + 10, y + 45)
        end

        -- Path input box
        setColor(THEME.panelBg)
        love.graphics.rectangle("fill", x + 10, y + 70, dialogW - 20, 30)
        setColor(THEME.border)
        love.graphics.rectangle("line", x + 10, y + 70, dialogW - 20, 30)

        -- Path text with cursor
        setColor(THEME.text)
        local displayPath = self.path
        local cursor = self.cursorVisible and "|" or ""
        love.graphics.setScissor(x + 12, y + 72, dialogW - 24, 26)
        love.graphics.print(displayPath .. cursor, x + 14, y + 76)
        love.graphics.setScissor()

        -- Extension hint
        setColor(THEME.textDim)
        if self.extension ~= "" then
            love.graphics.print("Extension: " .. self.extension, x + 10, y + 105)
        else
            love.graphics.print("Enter full path with extension", x + 10, y + 105)
        end

        -- Message
        if self.message ~= "" then
            setColor(THEME.selectionSolid)
            love.graphics.print(self.message, x + 10, y + 125)
        end

        -- Buttons hint
        setColor(THEME.textDim)
        love.graphics.print("Enter = Confirm | Escape = Cancel", x + 10, y + 170)
    end

    function self.update(dt)
        self.cursorTimer = self.cursorTimer + dt
        if self.cursorTimer >= 0.5 then
            self.cursorTimer = 0
            self.cursorVisible = not self.cursorVisible
        end
    end

    function self.textinput(t)
        if self.active then
            self.path = self.path .. t
            self.message = ""
        end
    end

    function self.keypressed(key)
        if not self.active then return end

        if key == "escape" then
            self.active = false
            if self.callback then
                self.callback(nil)  -- Cancelled
            end
        elseif key == "return" then
            local fullPath = self.path
            -- Only add extension if one is specified and path doesn't already have it
            if self.extension ~= "" and not fullPath:match("%." .. self.extension:gsub("%.", "") .. "$") then
                fullPath = fullPath .. self.extension
            end

            if self.mode == "save" then
                self.active = false
                if self.callback then
                    self.callback(fullPath)
                end
            else  -- load mode
                -- Check if file exists
                local file = io.open(fullPath, "rb")
                if file then
                    file:close()
                    self.active = false
                    if self.callback then
                        self.callback(fullPath)
                    end
                else
                    self.message = "File not found!"
                end
            end
        elseif key == "backspace" then
            self.path = self.path:sub(1, #self.path - 1)
            self.message = ""
        end
    end

    return self
end
