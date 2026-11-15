
canvas = love.graphics.newCanvas(800, 600)


training = 128
trainer_rounds = training

function math.sgn(x)
    return x/math.abs(x)
end

player_win = 0
bot_win = 0

function restart_game()
    player_x = 0
    bot_x = 0
    ball_x = 0
    ball_y = 0
    ball_xv = 0.005 * math.sgn(love.math.random() - 0.5)
    ball_yv = -0.005
    if trainer_rounds == 0 then print("Training complete! Now play with the AI!"); player_win = 0; bot_win = 0 end
end

rwd=0

function punish(v)

    local a, b, c, d
    rwd = rwd - 1

    for i=0, 256 do
        a = (love.math.random() * 2 - 1) * v
        b = (love.math.random() * 2 - 1) * v
        c = (love.math.random() * 2 - 1) * v
        d = (love.math.random() * 2 - 1) * v

        brain_neurons[0][0][0] = a
        brain_neurons[0][1][0] = b
        brain_neurons[0][2][0] = c
        brain_neurons[0][3][0] = d
        brain_neurons[0][4][0] = math.max(math.min(rwd, 1), -1)

        brain_draw()

        brain_think(0.2)

        brain_neurons[0][0][0] = a
        brain_neurons[0][1][0] = b
        brain_neurons[0][2][0] = c
        brain_neurons[0][3][0] = d
        brain_neurons[0][4][0] = math.max(math.min(rwd, 1), -1)

        brain_learn(0.0002*brain_error())
    end

    restart_game()
end

function reward(v)

    rwd = rwd + 1

    for i=0, 256 do
        brain_neurons[0][0][0] = v
        brain_neurons[0][1][0] = v
        brain_neurons[0][2][0] = v
        brain_neurons[0][3][0] = v
        brain_neurons[0][4][0] = math.max(math.min(rwd, 1), -1)

        brain_draw()

        brain_think(0.2)

        brain_neurons[0][0][0] = v
        brain_neurons[0][1][0] = v
        brain_neurons[0][2][0] = v
        brain_neurons[0][3][0] = v
        brain_neurons[0][4][0] = math.max(math.min(rwd, 1), -1)

        brain_learn(0.0002*brain_error())
    end
    restart_game()
end

restart_game()


function love.draw()


    -- collision detection
    if ball_x+ball_xv>1 then
        ball_x = 2-ball_x-ball_xv
        ball_xv=-ball_xv
    elseif ball_x+ball_xv<-1 then
        ball_x = -2-ball_x-ball_xv
        ball_xv=-ball_xv
    else
        ball_x = ball_x+ball_xv
    end
    if ball_y+ball_yv>1 then
        ball_y = 2-ball_y-ball_yv
        ball_yv=-ball_yv
        if ball_x>bot_x+0.125 or ball_x<bot_x-0.125 then punish(10); player_win = player_win + 1; return end
        ball_xv=(ball_xv+(ball_x-bot_x)*0.1)*0.5
        trainer_rounds = trainer_rounds - 1
        rwd = rwd + 0.5
        love.graphics.setCanvas(canvas)
        love.graphics.setColor(0.25, 0.25, 0.25, 1)
        love.graphics.line((training-trainer_rounds)*800/1024, 600, (training-trainer_rounds)*800/1024, 600-brain_error()*4)
        love.graphics.setCanvas()
    elseif ball_y+ball_yv<-1 then
        ball_y = -2-ball_y-ball_yv
        ball_yv=-ball_yv
        if ball_x>player_x+0.125 or ball_x<player_x-0.125 then reward(10); bot_win = bot_win + 1; return end
        ball_xv=(ball_xv+(ball_x-player_x)*0.1)*0.5
        rwd = rwd - 0.125
    else
        ball_y = ball_y+ball_yv
    end

    rwd = rwd + (4 - rwd) * 0.01

    love.graphics.setColor(1, 1, 1, 1)
    love.graphics.draw(canvas, 0, 0)
    love.graphics.rectangle("fill", 400+player_x * 384 - 50, 10,  100, 10)
    love.graphics.rectangle("fill", 400+bot_x    * 384 - 50, 580, 100, 10)

    love.graphics.circle("fill", 400 + ball_x * 384, 300 + ball_y * 264, 16)

    brain_neurons[0][0][0] = (bot_x+1)*8
    brain_neurons[0][1][0] = (player_x+1)*8
    brain_neurons[0][2][0] = (ball_x+1)*8
    brain_neurons[0][3][0] = (ball_y+1)*8
    brain_neurons[0][4][0] = math.max(math.min(rwd, 1), -1)

    love.graphics.print(trainer_rounds, 0, 0)
    love.graphics.print("l: "..player_win, 0, 16)
    love.graphics.print("w: "..bot_win, 0, 32)
    love.graphics.print("w/l: "..(bot_win/player_win), 0, 48)
    love.graphics.print("err: "..brain_error(), 0, 64)
    brain_draw()
    love.graphics.setColor(1,1,1,1)
    love.graphics.print("sx", brain_neurons[0][0][2], brain_neurons[0][0][3])
    love.graphics.print("px", brain_neurons[0][1][2], brain_neurons[0][1][3])
    love.graphics.print("bx", brain_neurons[0][2][2], brain_neurons[0][2][3])
    love.graphics.print("by", brain_neurons[0][3][2], brain_neurons[0][3][3])
    love.graphics.print("rw", brain_neurons[0][4][2], brain_neurons[0][4][3])
    love.graphics.print("lc", brain_neurons[0][brain_neuronCount - 1][2], brain_neurons[0][brain_neuronCount - 1][3])
    love.graphics.print("rc", brain_neurons[0][brain_neuronCount - 2][2], brain_neurons[0][brain_neuronCount - 2][3])

    if trainer_rounds > 0 then
        brain_neurons[0][brain_neuronCount - 2][0] = math.min(math.max(bot_x - ball_x - ball_xv * (1 - ball_y) / ball_yv, -1), 1) * 2
        brain_neurons[0][brain_neuronCount - 1][0] = math.min(math.max(ball_x - bot_x - ball_xv * (1 - ball_y) / ball_yv, -1), 1) * 2
    end

    brain_think(0.2)

    --  select case keydown(1)
    --    case 130:player_x=max(min(player_x-0.01, 1), -1)
    --    case 131:player_x=max(min(player_x+0.01, 1), -1)
    --  end select
    player_x = math.max(math.min(player_x +
    (
        -math.min(math.max(player_x - ball_x - ball_xv * (ball_y) / ball_yv, -1), 1) * 2 +
         math.min(math.max(ball_x - player_x - ball_xv * (ball_y) / ball_yv, -1), 1) * 2 +
         love.math.random()*0.1*(player_win+1)/(bot_win+1)
    ) * 0.016, 1), -1)

    if trainer_rounds > 0 then
        brain_neurons[0][brain_neuronCount-2][0] = math.min(math.max(bot_x - ball_x - ball_xv * (1 - ball_y) / ball_yv, -1), 1)
        brain_neurons[0][brain_neuronCount-1][0] = math.min(math.max(ball_x - bot_x - ball_xv * (1 - ball_y) / ball_yv, -1), 1)
    end

    l = brain_neurons[0][brain_neuronCount-2][0]
    r = brain_neurons[0][brain_neuronCount-1][0]

    bot_x=math.max(math.min(bot_x+(-l+r)*0.03, 1), -1)

    brain_neurons[0][0][0] = bot_x
    brain_neurons[0][1][0] = player_x
    brain_neurons[0][2][0] = ball_x
    brain_neurons[0][3][0] = ball_y
    brain_neurons[0][4][0] = math.max(math.min(rwd, 1), -1)

    if trainer_rounds > 0 then
        brain_learn(0.0002*brain_error())
    else
        brain_learn(0.00002*brain_error())
    end

end