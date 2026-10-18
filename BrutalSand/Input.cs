using Brutal.GlfwApi;

public class Input
{
    private readonly HashSet<GlfwKey> _keysPressed = [];
    private readonly HashSet<GlfwMouseButton> _buttonsPressed = [];

    public Input(GlfwWindow window)
    {
        window.OnKey += (_, key, _, action, _) =>
        {
            // only the first press
            if (action == GlfwKeyAction.Press)
            {
                _keysPressed.Add(key);
            }
        };

        window.OnMouseButton += (_, button, action, _) =>
        {
            if (action == GlfwButtonAction.Press)
            {
                _buttonsPressed.Add(button);
            }
        };
    }

    // call before PollEvents so only this frames presses are kept
    public void NewFrame()
    {
        _keysPressed.Clear();
        _buttonsPressed.Clear();
    }

    public bool WasPressed(GlfwKey key) => _keysPressed.Contains(key);
    public bool WasPressed(GlfwMouseButton button) => _buttonsPressed.Contains(button);
}