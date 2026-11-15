function love.conf(t)
    lua = {}
    t.window.width = 800
    t.window.height = 600
    lua.window = {}
    lua.window.getWidth = t.window.width
    lua.window.getHeight = t.window.height
end
