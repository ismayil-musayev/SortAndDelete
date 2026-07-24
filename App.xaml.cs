namespace SortAndDelete;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		// The whole UI is designed dark — photos pop on a dark canvas.
		UserAppTheme = AppTheme.Dark;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
