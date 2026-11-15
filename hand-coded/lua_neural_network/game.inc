trainer_rounds = 5

sub restart_game
  player_x = 0
  bot_x = 0
  ball_x = 0
  ball_y = 0
  ball_xv = 0.005*sgn(rnd-0.5)
  ball_yv = -0.005
  if trainer_rounds = 0 then ? "Training complete! Now play with the AI!"
  goto start:
end sub

rwd=0

sub punish v

  local a, b, c, d
  rwd = rwd - 1

  for i=0 to 64
    a=(rnd*2-1)*v:b=(rnd*2-1)*v:c=(rnd*2-1)*v:d=(rnd*2-1)*v

    brain_neurons(0, 0, 0) = a
    brain_neurons(0, 1, 0) = b
    brain_neurons(0, 2, 0) = c
    brain_neurons(0, 3, 0) = d
    brain_neurons(0, 4, 0) = max(min(rwd, 1), -1)

    brain_draw

    brain_think(0.001)

    brain_neurons(0, 0, 0) = a
    brain_neurons(0, 1, 0) = b
    brain_neurons(0, 2, 0) = c
    brain_neurons(0, 3, 0) = d
    brain_neurons(0, 4, 0) = max(min(rwd, 1), -1)

    brain_learn(0.01)
  next

  restart_game
end sub

sub reward v

  rwd = rwd + 1

  for i=0 to 64
    brain_neurons(0, 0, 0) = v
    brain_neurons(0, 1, 0) = v
    brain_neurons(0, 2, 0) = v
    brain_neurons(0, 3, 0) = v
    brain_neurons(0, 4, 0) = max(min(rwd, 1), -1)

    brain_draw

    brain_think(0.001)

    brain_neurons(0, 0, 0) = v
    brain_neurons(0, 1, 0) = v
    brain_neurons(0, 2, 0) = v
    brain_neurons(0, 3, 0) = v
    brain_neurons(0, 4, 0) = max(min(rwd, 1), -1)

    brain_learn(0.01)
  next

  restart_game
end sub

restart_game

start:

page write 1

do


  if ball_x+ball_xv>1 then
    ball_x = 2-ball_x-ball_xv
    ball_xv=-ball_xv
  elseif ball_x+ball_xv<-1 then
    ball_x = -2-ball_x-ball_xv
    ball_xv=-ball_xv
  else
    ball_x = ball_x+ball_xv
  endif
  if ball_y+ball_yv>1 then
    ball_y = 2-ball_y-ball_yv
    ball_yv=-ball_yv
    if ball_x>bot_x+0.125 or ball_x<bot_x-0.125 then punish 10
    ball_xv=(ball_x-bot_x)*0.1
    trainer_rounds = trainer_rounds - 1
    rwd = rwd + 0.5
  elseif ball_y+ball_yv<-1 then
    ball_y = -2-ball_y-ball_yv
    ball_yv=-ball_yv
    if ball_x>player_x+0.125 or ball_x<player_x-0.125 then reward 10
    ball_xv=(ball_x-player_x)*0.1
    rwd = rwd - 0.125
  else
    ball_y = ball_y+ball_yv
  endif

  rwd = rwd + (4 - rwd)*0.01


  box 400+player_x*384-50, 10,  100, 10,,, rgb(white)
  box 400+bot_x   *384-50, 580, 100, 10,,, rgb(white)

  circle 400+ball_x*384, 300+ball_y*264, 16,,,, rgb(white)

  brain_neurons(0, 0, 0) = (bot_x+1)*8
  brain_neurons(0, 1, 0) = (player_x+1)*8
  brain_neurons(0, 2, 0) = (ball_x+1)*8
  brain_neurons(0, 3, 0) = (ball_y+1)*8
  brain_neurons(0, 4, 0) = max(min(rwd, 1), -1)

  ? trainer_rounds
  brain_draw
  text brain_neurons(0, 0, 2), brain_neurons(0, 0, 3), "sx"
  text brain_neurons(0, 1, 2), brain_neurons(0, 1, 3), "px"
  text brain_neurons(0, 2, 2), brain_neurons(0, 2, 3), "bx"
  text brain_neurons(0, 3, 2), brain_neurons(0, 3, 3), "by"
  text brain_neurons(0, 4, 2), brain_neurons(0, 4, 3), "rw"
  text brain_neurons(0, brain_neuronCount-1, 2), brain_neurons(0, brain_neuronCount-1, 3), "lc"
  text brain_neurons(0, brain_neuronCount-2, 2), brain_neurons(0, brain_neuronCount-2, 3), "rc"
  page copy 1 to 0
  cls


  if trainer_rounds > 0 then
    brain_neurons(0, brain_neuronCount-2, 0) = min(max(bot_x-ball_x-ball_xv*(1-ball_y)/ball_yv, -1), 1)*2
    brain_neurons(0, brain_neuronCount-1, 0) = min(max(ball_x-bot_x+ball_xv*(1-ball_y)/ball_yv, -1), 1)*2
  endif

  brain_think(0.04)

'  select case keydown(1)
'    case 130:player_x=max(min(player_x-0.01, 1), -1)
'    case 131:player_x=max(min(player_x+0.01, 1), -1)
'  end select
  player_x=max(min(player_x+(-min(max(player_x-ball_x, -1), 1)*2+min(max(ball_x-player_x, -1), 1)*2)*0.01, 1), -1)

  if trainer_rounds > 0 then
    brain_neurons(0, brain_neuronCount-2, 0) = min(max(bot_x-ball_x-ball_xv*(1-ball_y)/ball_yv, -1), 1)*2
    brain_neurons(0, brain_neuronCount-1, 0) = min(max(ball_x-bot_x+ball_xv*(1-ball_y)/ball_yv, -1), 1)*2
  endif

  l = brain_neurons(0, brain_neuronCount-2, 0)
  r = brain_neurons(0, brain_neuronCount-1, 0)

  bot_x=max(min(bot_x+(-l+r)*0.01, 1), -1)

  brain_neurons(0, 0, 0) = bot_x
  brain_neurons(0, 1, 0) = player_x
  brain_neurons(0, 2, 0) = ball_x
  brain_neurons(0, 3, 0) = ball_y
  brain_neurons(0, 4, 0) = max(min(rwd, 1), -1)

  if trainer_rounds > 0 then
    brain_learn(0.1)
  else
    brain_learn(0.001)
  endif

loop
