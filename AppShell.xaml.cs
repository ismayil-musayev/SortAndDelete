using SortAndDelete.Views;

namespace SortAndDelete;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		Routing.RegisterRoute(nameof(SwipePage), typeof(SwipePage));
		Routing.RegisterRoute(nameof(BinPage), typeof(BinPage));
	}
}
