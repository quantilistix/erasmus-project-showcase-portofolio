-- LuaTracker - A tracker-style music application
-- Main entry point

function love.load()
    love.keyboard.setKeyRepeat(true)

    -- Load modules
    require("theme")
    require("commands")
    require("tracker")
    require("fileIO")
    require("button")
    require("panel")
    require("scrollList")
    require("patternList")
    require("menuBar")
    require("instrumentList")
    require("instrumentDetails")
    require("waveform")
    require("noteGrid")
    require("titleBar")
    require("fileDialog")
    require("variablesPanel")

    -- Initialize tracker
    Tracker:init()

    -- Create UI components (will be positioned in resize)
    TitleBarComponent = newTitleBar(0, 0, 1024, 28)

    PatternListComponent = newPatternList(0, 28, 150, 200)

    MenuBarComponent = newMenuBar(150, 28, 200, 80)
    MenuBarComponent.onAction = handleMenuAction

    VariablesPanelComponent = newVariablesPanel(350, 28, 300, 60)

    InstrumentListComponent = newInstrumentList(824, 28, 200, 250)

    WaveformComponent = newWaveformDisplay(0, 228, 824, 80, 4)

    InstrumentDetailsComponent = newInstrumentDetails(824, 278, 200, 490)

    NoteGridComponent = newNoteGrid(0, 308, 824, 460)

    -- File dialog (nil when not active)
    CurrentFileDialog = nil

    -- Layout components
    love.resize(love.graphics.getDimensions())
end

function love.resize(w, h)
    -- Right column width
    local rightWidth = 200
    -- Left+center width (everything except right column)
    local leftCenterWidth = w - rightWidth

    -- Title bar row
    TitleBarComponent.setPosition(0, 0)
    TitleBarComponent.setSize(w, 28)

    -- Row 2: Pattern list + Menu bar + Variables | Instrument list
    local row2Y = 28
    local patternListWidth = 150
    local patternListHeight = 180
    local menuBarWidth = 200
    local menuBarHeight = 80

    PatternListComponent.setPosition(0, row2Y)
    PatternListComponent.setSize(patternListWidth, patternListHeight)

    MenuBarComponent.setPosition(patternListWidth, row2Y)
    MenuBarComponent.setSize(menuBarWidth, menuBarHeight)

    -- Variables panel fills space between menu bar and instrument list
    local varPanelX = patternListWidth + menuBarWidth
    local varPanelW = leftCenterWidth - patternListWidth - menuBarWidth
    VariablesPanelComponent.setPosition(varPanelX, row2Y)
    VariablesPanelComponent.setSize(varPanelW, menuBarHeight)

    InstrumentListComponent.setPosition(w - rightWidth, row2Y)
    InstrumentListComponent.setSize(rightWidth, 250)

    -- Waveforms: horizontal bar spanning left+center, below pattern list and menu bar
    local waveformY = row2Y + math.max(patternListHeight, menuBarHeight)
    local waveformH = 80

    -- Calculate waveform width based on channel count
    local minChannelWidth = 100
    local totalChannelsWidth = Tracker.channelCount * minChannelWidth
    local waveformW = math.max(leftCenterWidth, totalChannelsWidth)

    WaveformComponent.setPosition(0, waveformY)
    WaveformComponent.setSize(waveformW, waveformH)
    WaveformComponent.channelCount = Tracker.channelCount

    -- Right column below instrument list: Instrument details
    local detailsY = row2Y + 250
    local detailsH = h - detailsY
    InstrumentDetailsComponent.setPosition(w - rightWidth, detailsY)
    InstrumentDetailsComponent.setSize(rightWidth, detailsH)

    -- Note grid: touches left edge, below waveforms
    local gridY = waveformY + waveformH
    local gridH = h - gridY
    NoteGridComponent.setPosition(0, gridY)
    NoteGridComponent.setSize(leftCenterWidth, gridH)
end

function love.update(dt)
    Tracker:update(dt)

    if CurrentFileDialog and CurrentFileDialog.active then
        CurrentFileDialog.update(dt)
    end
end

function love.draw()
    -- Clear with theme background
    setColor(THEME.bg)
    love.graphics.rectangle("fill", 0, 0, love.graphics.getDimensions())

    -- Draw all components
    TitleBarComponent.draw()
    PatternListComponent.draw()
    MenuBarComponent.draw()
    VariablesPanelComponent.draw()
    InstrumentListComponent.draw()
    WaveformComponent.draw()
    InstrumentDetailsComponent.draw()
    NoteGridComponent.draw()

    -- Draw file dialog on top if active
    if CurrentFileDialog and CurrentFileDialog.active then
        CurrentFileDialog.draw()
    end

    -- Playing indicator
    if Tracker.playing then
        setColor(THEME.selectionSolid)
        love.graphics.print("PLAYING", 160, 32)
    end
end

function love.mousepressed(x, y, button, istouch)
    if CurrentFileDialog and CurrentFileDialog.active then
        return  -- Block input while dialog is open
    end

    TitleBarComponent.mousepressed(x, y, button, istouch)
    PatternListComponent.mousepressed(x, y, button, istouch)
    MenuBarComponent.mousepressed(x, y, button, istouch)
    VariablesPanelComponent.mousepressed(x, y, button, istouch)
    InstrumentListComponent.mousepressed(x, y, button, istouch)
    InstrumentDetailsComponent.mousepressed(x, y, button, istouch)
    NoteGridComponent.mousepressed(x, y, button, istouch)
end

function love.mousereleased(x, y, button, istouch)
    if CurrentFileDialog and CurrentFileDialog.active then
        return
    end

    TitleBarComponent.mousereleased(x, y, button, istouch)
    MenuBarComponent.mousereleased(x, y, button, istouch)
    InstrumentDetailsComponent.mousereleased(x, y, button, istouch)
end

function love.wheelmoved(x, y)
    if CurrentFileDialog and CurrentFileDialog.active then
        return
    end

    PatternListComponent.wheelmoved(x, y)
    InstrumentListComponent.wheelmoved(x, y)
    NoteGridComponent.wheelmoved(x, y)
end

function love.keypressed(key, scancode, isRepeat)
    if CurrentFileDialog and CurrentFileDialog.active then
        CurrentFileDialog.keypressed(key)
        return
    end

    NoteGridComponent.keypressed(key, scancode, isRepeat)

    -- Global shortcuts
    if key == "f5" then
        Tracker:play()
    elseif key == "f6" then
        Tracker:stop()
    elseif key == "f11" then
        love.window.setFullscreen(not love.window.getFullscreen())
    end
end

function love.textinput(t)
    if CurrentFileDialog and CurrentFileDialog.active then
        CurrentFileDialog.textinput(t)
    end
end

-- Menu action handler
function handleMenuAction(action)
    if action == "start" then
        Tracker:play()
    elseif action == "stop" then
        Tracker:stop()
    elseif action == "new" then
        Tracker:init()
        PatternListComponent.selectedIndex = 1
    elseif action == "load_module" then
        CurrentFileDialog = newFileDialog("load", "Load Module", ".ltm", function(path)
            CurrentFileDialog = nil
            if path then
                local success, err = FileIO.loadModule(path)
                if not success then
                    print("Error loading module: " .. (err or "unknown"))
                else
                    -- Refresh layout for new channel count
                    love.resize(love.graphics.getDimensions())
                end
            end
        end)
    elseif action == "save_module" then
        CurrentFileDialog = newFileDialog("save", "Save Module", ".ltm", function(path)
            CurrentFileDialog = nil
            if path then
                local success, err = FileIO.saveModule(path)
                if not success then
                    print("Error saving module: " .. (err or "unknown"))
                end
            end
        end)
    elseif action == "import_pattern" then
        CurrentFileDialog = newFileDialog("load", "Import Pattern", ".ltp", function(path)
            CurrentFileDialog = nil
            if path then
                local success, idx = FileIO.importPattern(path)
                if success then
                    table.insert(Tracker.playOrder, idx)
                else
                    print("Error importing pattern: " .. (idx or "unknown"))
                end
            end
        end)
    elseif action == "export_pattern" then
        CurrentFileDialog = newFileDialog("save", "Export Pattern", ".ltp", function(path)
            CurrentFileDialog = nil
            if path then
                local patternIdx = Tracker.playOrder[PatternListComponent.selectedIndex] or 0
                local success, err = FileIO.exportPattern(path, patternIdx)
                if not success then
                    print("Error exporting pattern: " .. (err or "unknown"))
                end
            end
        end)
    elseif action == "load_instrument" then
        CurrentFileDialog = newFileDialog("load", "Load Instrument (wav/ogg/mp3)", "", function(path)
            CurrentFileDialog = nil
            if path then
                local idx = Tracker:loadInstrument(path)
                if idx then
                    InstrumentListComponent.selectInstrument(idx)
                else
                    print("Error loading instrument")
                end
            end
        end)
    elseif action == "about" then
        print("LuaTracker pro (test)")
        print("A tracker-style music application")
        print("Built with Love2D")
    elseif action == "controls" then
        print("=== Controls ===")
        print("Arrow keys: Navigate tracker")
        print("Tab: Next channel")
        print("AWSEDFTGYHUJ: Piano keys (C to B)")
        print("B: Stop note (when recording)")
        print("Backspace: Clear cell")
        print("Delete: Clear cell and move down")
        print("Space: Play/Stop")
        print("F5: Play | F6: Stop | F11: Fullscreen")
    elseif action == "cmd_debug" then
        Commands.debug = not Commands.debug
        print("Command debug: " .. (Commands.debug and "ON" or "OFF"))
    end
end
