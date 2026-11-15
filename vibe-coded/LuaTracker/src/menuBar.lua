-- Menu bar with sub-menus arranged in grid

---@param x integer
---@param y integer
---@param w integer
---@param h integer
---@return menuBar
function newMenuBar(x, y, w, h)

    ---@class menuBar
    local self = {
        x = x,
        y = y,
        w = w,
        h = h,
        buttons = {},
        currentMenu = "main",
        onAction = nil,
    }

    -- Define menu structure
    self.menus = {
        main = {
            {text = "Play", action = "start"},
            {text = "Stop", action = "stop"},
            {text = "File", action = "submenu", target = "file"},
            {text = "Help", action = "submenu", target = "help"},
        },
        help = {
            {text = "< Back", action = "submenu", target = "main"},
            {text = "About", action = "about"},
            {text = "Controls", action = "controls"},
            {text = "Cmd Debug", action = "cmd_debug"},
        },
        file = {
            {text = "< Back", action = "submenu", target = "main"},
            {text = "New", action = "new"},
            {text = "Load Mod", action = "load_module"},
            {text = "Save Mod", action = "save_module"},
            {text = "Load Inst", action = "load_instrument"},
            {text = "Imp Pat", action = "import_pattern"},
            {text = "Exp Pat", action = "export_pattern"},
        },
    }

    function self.buildButtons()
        self.buttons = {}
        local menu = self.menus[self.currentMenu]
        if not menu then return end

        local itemCount = #menu
        local padding = 4
        local availW = self.w - padding * 2
        local availH = self.h - padding * 2

        -- 2x2 grid for main, more rows for others
        local cols = 2
        local rows = math.ceil(itemCount / cols)

        local btnW = math.floor(availW / cols) - 2
        local btnH = math.floor(availH / rows) - 2

        for i, item in ipairs(menu) do
            local col = (i - 1) % cols
            local row = math.floor((i - 1) / cols)
            local btnX = self.x + padding + col * (btnW + 2)
            local btnY = self.y + padding + row * (btnH + 2)

            local btn = newButton(item.text, btnX, btnY, btnW, btnH, function()
                if item.action == "submenu" then
                    self.currentMenu = item.target
                    self.buildButtons()
                elseif self.onAction then
                    self.onAction(item.action)
                end
            end, true)
            table.insert(self.buttons, btn)
        end
    end

    function self.draw()
        -- Background
        drawPanel(self.x, self.y, self.w, self.h, false)

        -- Draw buttons
        for _, btn in ipairs(self.buttons) do
            btn.draw()
        end
    end

    function self.mousepressed(mx, my, button, istouch)
        for _, btn in ipairs(self.buttons) do
            btn.mousepressed(mx, my, button, istouch)
        end
    end

    function self.mousereleased(mx, my, button, istouch)
        for _, btn in ipairs(self.buttons) do
            btn.mousereleased(mx, my, button, istouch)
        end
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

    self.buildButtons()

    return self
end
