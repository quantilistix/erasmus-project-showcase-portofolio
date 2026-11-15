cls

function tanh(x)
  if x>10 then tanh = 1:exit function
  if x<-10 then tanh = -1:exit function
  tanh = (exp(2*x) - 1) / (exp(2*x) + 1)
end function

sub brain_init

  brain_neuronCount = 5+16+16+2

  dim float brain_neurons(1, brain_neuronCount-1, 3)

  brain_weightCount = 5*16 + 16*16 + 16*2
  dim float brain_weights(brain_weightCount-1, 2)


  for neuron=0 to brain_neuronCount-1
    brain_neurons(0, neuron, 0)=0
    brain_neurons(0, neuron, 1)=0
    brain_neurons(0, neuron, 2)=rnd*256+400-128
    brain_neurons(0, neuron, 3)=rnd*256+128-128
    brain_neurons(1, neuron, 0)=brain_neurons(0, neuron, 0)
    brain_neurons(1, neuron, 1)=brain_neurons(0, neuron, 1)
    brain_neurons(1, neuron, 2)=brain_neurons(0, neuron, 2)
    brain_neurons(1, neuron, 3)=brain_neurons(0, neuron, 3)
  next

  weight = 0

  for i=0 to 4
    for j=0 to 15
      brain_weights(weight, 0)=rnd*8-4
      brain_weights(weight, 1)=i
      brain_weights(weight, 2)=j+5
      weight=weight+1
    next
  next

  for i=0 to 15
    for j=0 to 15
      brain_weights(weight, 0)=rnd*8-4
      brain_weights(weight, 1)=i+5
      brain_weights(weight, 2)=j+5+16
      weight=weight+1
    next
  next

  for i=0 to 15
    for j=0 to 1
      brain_weights(weight, 0)=rnd*8-4
      brain_weights(weight, 1)=i+5+16
      brain_weights(weight, 2)=j+5+16+16
      weight=weight+1
    next
  next

  local dx, dy, dist, force

  ? "Calibrating neurons..."
  for t=0 to 255

    box int(10+t*780/256), 20, int(10+(t+1)*780/256)-int(10+t*780/256)-1, 10,,, rgb(white)
    for i=0 to brain_neuronCount-2
      for j=i+1 to brain_neuronCount-1
        dx = brain_neurons(0, i, 2) - brain_neurons(0, j, 2)
        dy = brain_neurons(0, i, 3) - brain_neurons(0, j, 3)
        dist = sqr(dx*dx+dy*dy)
        force = (dist-128)*0.01/dist
        brain_neurons(1, i, 2) = brain_neurons(0, i, 2) - force*dx
        brain_neurons(1, i, 3) = brain_neurons(0, i, 3) - force*dy
        brain_neurons(1, j, 2) = brain_neurons(0, j, 2) + force*dx
        brain_neurons(1, j, 3) = brain_neurons(0, j, 3) + force*dy
        brain_neurons(0, i, 2) = brain_neurons(1, i, 2)
        brain_neurons(0, i, 3) = brain_neurons(1, i, 3)
        brain_neurons(0, j, 2) = brain_neurons(1, j, 2)
        brain_neurons(0, j, 3) = brain_neurons(1, j, 3)
      next
    next

  next
end sub

sub brain_think(dt)

  for weight=0 to brain_weightCount-1
    brain_neurons(1, brain_weights(weight, 1), 0)=brain_neurons(1, brain_weights(weight, 1), 0)+brain_neurons(0, brain_weights(weight, 2), 1)*brain_weights(weight, 0)*dt
    brain_neurons(1, brain_weights(weight, 2), 1)=brain_neurons(1, brain_weights(weight, 2), 1)-brain_neurons(0, brain_weights(weight, 1), 0)*brain_weights(weight, 0)*dt
  next
  for neuron=0 to brain_neuronCount-1
    brain_neurons(0, neuron, 0)=tanh(brain_neurons(1, neuron, 0)-brain_neurons(0, neuron, 1)*dt)
    brain_neurons(0, neuron, 1)=tanh(brain_neurons(1, neuron, 1)+brain_neurons(0, neuron, 0)*dt)
    brain_neurons(1, neuron, 0)=brain_neurons(0, neuron, 0)
    brain_neurons(1, neuron, 1)=brain_neurons(0, neuron, 1)
  next

end sub

sub brain_learn(dt)
  for weight=0 to brain_weightCount-1
    brain_weights(weight, 0) = brain_weights(weight, 0)+(brain_neurons(0, brain_weights(weight, 2), 1)-brain_neurons(0, brain_weights(weight, 2), 0))*dt
  next
end sub

sub brain_draw

  local a, e, w
  for weight=0 to brain_weightCount-1
    w = max(min((brain_weights(weight, 0)+1)*128, 255), 0)
    line brain_neurons(0, brain_weights(weight, 1), 2), brain_neurons(0, brain_weights(weight, 1), 3), brain_neurons(0, brain_weights(weight, 2), 2), brain_neurons(0, brain_weights(weight, 2), 3),, rgb(255-w, 255-abs(w-127.5)*2, w)
  next
  for neuron=0 to brain_neuronCount-1
    a = min(max((brain_neurons(0, neuron, 0)+1)*128, 0),255)
    e = min(max((brain_neurons(0, neuron, 1)+1)*128, 0),255)
    circle brain_neurons(0, neuron, 2), brain_neurons(0, neuron, 3), 4,,, rgb(white), rgb(e,0,a)
  next

end sub


