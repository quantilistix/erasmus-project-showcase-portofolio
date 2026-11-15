#include "pch.hpp"
#include "glinit.hpp"

int GLinit(GLFWwindow *&window, GLFWmonitor *&monitor, GLFWvidmode *&mode, int *windowWidth, int *windowHeight, int *max_width, int *max_height)
{
    // Initialize GLFW and check for initialization errors
    if (!glfwInit())
    {
        std::cerr << "ERROR::GLFW::INIT_FAILED: " << std::endl;
        return -1;
    }
    else
    {
        std::cout << "GLFW::INIT_SUCCESSFUL!" << std::endl;
    }
    // Set GLFW to use OpenGL 3.3 core profile
    glfwWindowHint(GLFW_CONTEXT_VERSION_MAJOR, 4);
    glfwWindowHint(GLFW_CONTEXT_VERSION_MINOR, 6);
    glfwWindowHint(GLFW_OPENGL_PROFILE, GLFW_OPENGL_CORE_PROFILE);
    glfwWindowHint(GLFW_OPENGL_FORWARD_COMPAT, GL_TRUE);
    glfwWindowHint(GLFW_DEPTH_BITS, 24);
    glfwWindowHint(GLFW_STENCIL_BITS, 8);

    // Create a cetered, windowed mode window and its OpenGL context
    window = glfwCreateWindow(*windowWidth, *windowHeight, "Numjri laboratories", NULL, NULL);
    monitor = glfwGetPrimaryMonitor();
    mode = (GLFWvidmode *)glfwGetVideoMode(monitor);

    // Get screen width and height
    *max_width = mode->width;
    *max_height = mode->height;
    glfwSetWindowMonitor(window, NULL, (*max_width / 2) - (*windowWidth / 2), (*max_height / 2) - (*windowHeight / 2), *windowWidth, *windowHeight, GLFW_DONT_CARE);
    // Check for window initialization errors
    if (!window)
    {
        std::cerr << "ERROR::GLFW::WINDOW::CREATE_FAILED: \n"
                  << std::endl;
        glfwTerminate();
        return -1;
    }
    else
    {
        std::cout << "GLFW::WINDOW::CREATE_SUCCESSFUL!" << std::endl;
    }
    // Make the window's context current
    glfwMakeContextCurrent(window);

    // Check if window context is current
    if (!glfwGetCurrentContext())
    {
        std::cerr << "ERROR::GLFW::CONTEXT::NOT_CURRENT: \n"
                  << std::endl;
        glfwTerminate();
        return -1;
    }
    else
    {
        std::cout << "GLFW::CONTEXT::CURRENT!" << std::endl;
    }

    // Initialize GLAD and check for initialization errors
    if (!gladLoadGLLoader((GLADloadproc)glfwGetProcAddress))
    {
        std::cerr << "Failed to initialize GLAD: \n"
                  << std::endl;
        return -1;
    }
    else
    {
        std::cout << "GLAD::INIT::SUCCESSFUL!" << std::endl;
    }
    glViewport(0, 0, *windowWidth, *windowHeight);

    // Check for any OpenGL errors
    GLenum error = glGetError();
    if (error != GL_NO_ERROR)
    {
        std::cerr << "OpenGL Error during initialization: \n"
                  << error << "\n";
        return -1;
    }

    glfwRestoreWindow(window);

    std::cout << "Window created!\n"
              << std::endl;

    return 0;
}