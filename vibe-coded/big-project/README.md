# Introduction #

This is a mix of vibe coded (using AI) projects coded in C#.

# -------------------------------------------------------------------- #

# MYT.Kit #

MYT.Kit is a subset of the MYT game engine which is writen by me with AI, of course. It contains all of the code and implementations of physics (AVBD - augmented vertex block descent) and neural networks, more specifically, predictive coding (not backpropagation).

# MYT.IKit #

MYT.IKit is a subset of the MYT game engine that contains all of the interfaces and abstraction. The reason this is a separate thing is because if people find the abstractions to be good, then they'll use them but write other implentations. This also allows for other libraries to be compatible with MYT.Kit.

# Tester #

Tester is a program that showcases the physics engine. It has a custom made map that I did in Blender a while ago onto which multiple rigid bodies fall and a single soft body. This showcases the interactions between different body types, the continuous collision detection and AVBD physics.

# VoxelGame #

This is my biggest project so far. It's Minecraft, but with physics. Most of the physics runs on the GPU for high performance, though it may heavily lag. The title has been replaced with something else as I do not wish to share it for personal reasons.

# BrainEditor #

This is an editor for editing neural network graphs. This only works because of predictive coding. Predictive coding is a kind of neural network where each neuron has an error neuron pair. Each neuron's activation evolves over time under the rule:\
dx_i/dt = sum(x_j*w_i_j)

where x_i is the actiavtion of the neuron in interest, x_j is any neuron connected to it and w_i_j is the weight of the connection.

The wiring is a little more complicated than that, but the key difference is that learning happens in parallel at the level of the neuron. The weight update rule depends only on localized activations, making predictive coding highly parallelizable and allowing the network to learn in real time while it's thinking. There's a lot more to work on though.

# ArcadeLab #

This is seing a predictive coding neural network in action, trying to beat a maze game.

# The rest #

The rest are projects the AI wrote for itself for testing during development, so they don't matter.

