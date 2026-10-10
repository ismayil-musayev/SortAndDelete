using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class VisitScopeTests
{
    [Fact]
    public void A_page_is_on_no_visit_until_it_enters_one()
    {
        var page = new VisitScope();

        Assert.False(page.IsLive);
        Assert.True(page.Current.IsCancellationRequested);

        page.Leave(); // an OnDisappearing with no OnAppearing before it

        Assert.False(page.IsLive);
    }

    [Fact]
    public void Entering_starts_a_visit_and_leaving_cancels_it()
    {
        var page = new VisitScope();

        var visit = page.Enter();

        Assert.True(page.IsLive);
        Assert.Equal(visit, page.Current);
        Assert.False(visit.IsCancellationRequested);

        page.Leave();

        Assert.False(page.IsLive);
        Assert.True(visit.IsCancellationRequested);
        Assert.True(page.Current.IsCancellationRequested);
    }

    /// <summary>
    /// Replaced by a second visit, the first one's scan would have nothing left to stop it: the
    /// leave that follows would cancel the second only.
    /// </summary>
    [Fact]
    public void Entering_again_while_on_a_visit_rejoins_it()
    {
        var page = new VisitScope();

        var visit = page.Enter();
        var again = page.Enter();

        Assert.Equal(visit, again);

        page.Leave();

        Assert.True(visit.IsCancellationRequested);
    }

    [Fact]
    public void Entering_after_leaving_starts_a_visit_of_its_own()
    {
        var page = new VisitScope();

        var first = page.Enter();
        page.Leave();
        var second = page.Enter();

        Assert.NotEqual(first, second);
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.True(page.IsLive);
    }

    [Fact]
    public void One_page_leaving_doesn_t_end_another_page_s_visit()
    {
        var oldPage = new VisitScope();
        var newPage = new VisitScope();

        var oldVisit = oldPage.Enter();
        var newVisit = newPage.Enter();
        oldPage.Leave();

        Assert.True(oldVisit.IsCancellationRequested);
        Assert.False(newVisit.IsCancellationRequested);
    }
}
