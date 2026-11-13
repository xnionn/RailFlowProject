using System.Collections.Immutable;
using Bunit;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;
using RailFlow.Web.Components.Shared;

namespace RailFlow.Tests;

/// <summary>
/// Component tests for the shared Blazor components, rendered in memory with bUnit (no browser, no server).
/// Each test sets the parameters a page would pass and checks the markup and the callbacks.
/// </summary>
public sealed class ComponentTests : BunitContext
{
    // ----- StatCard -----

    [Fact]
    public void StatCard_ShowsLabelValueAndHint_WithToneAsCssClass()
    {
        var cut = Render<StatCard>(p => p
            .Add(c => c.Label, "Distance")
            .Add(c => c.Value, "460 km")
            .Add(c => c.Hint, "London to Paris")
            .Add(c => c.Tone, "ok"));

        var card = cut.Find("div.stat");
        Assert.Contains("ok", card.ClassList);
        Assert.Equal("Distance", cut.Find(".stat-label").TextContent);
        Assert.Equal("460 km", cut.Find(".stat-value").TextContent);
        Assert.Equal("London to Paris", cut.Find(".stat-hint").TextContent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StatCard_WithoutHint_DoesNotRenderTheHintLine(string? hint)
    {
        var cut = Render<StatCard>(p => p
            .Add(c => c.Label, "Price")
            .Add(c => c.Value, "€101.20")
            .Add(c => c.Hint, hint));

        Assert.Empty(cut.FindAll(".stat-hint"));
        Assert.Equal(new[] { "stat" }, cut.Find("div.stat").ClassList);
    }

    // ----- PipelineSteps -----

    private static ImmutableList<PipelineStepDiagnostic> FourSteps() =>
    [
        new("Resolve stations", StepStatus.Something, "LONDON, PARIS", 1),
        new("Plan route", StepStatus.Nothing, "No connection", 2),
        new("Check traffic", StepStatus.Error, "Timeout", 3),
        new("Price ticket", StepStatus.Skipped, "Not run", 0)
    ];

    [Fact]
    public void PipelineSteps_RendersOneNumberedButtonPerStep_WithStatusLabelAndTone()
    {
        var cut = Render<PipelineSteps>(p => p.Add(c => c.Diagnostics, FourSteps()));

        var buttons = cut.FindAll("button.step");
        Assert.Equal(4, buttons.Count);

        Assert.Equal(new[] { "1", "2", "3", "4" }, cut.FindAll(".step-num").Select(n => n.TextContent));
        Assert.Equal(
            new[] { "Resolve stations", "Plan route", "Check traffic", "Price ticket" },
            cut.FindAll(".step-title").Select(t => t.TextContent));

        var details = cut.FindAll(".step-detail").Select(d => d.TextContent).ToList();
        Assert.StartsWith("Done", details[0]);
        Assert.StartsWith("No result", details[1]);
        Assert.StartsWith("Failed", details[2]);
        Assert.StartsWith("Skipped", details[3]);
        Assert.Contains("2 ms", details[1]);

        Assert.Contains("ok", buttons[0].ClassList);
        Assert.Contains("warn", buttons[1].ClassList);
        Assert.Contains("err", buttons[2].ClassList);
        Assert.DoesNotContain("ok", buttons[3].ClassList);
    }

    [Fact]
    public void PipelineSteps_EveryStatusHasItsOwnLabel()
    {
        var steps = Enum.GetValues<StepStatus>()
            .Select(status => new PipelineStepDiagnostic(status.ToString(), status, "", 0))
            .ToImmutableList();

        var cut = Render<PipelineSteps>(p => p.Add(c => c.Diagnostics, steps));

        var labels = cut.FindAll(".step-detail").Select(d => d.TextContent.Split(" · ")[0]).ToList();
        Assert.Equal(steps.Count, labels.Distinct().Count());
    }

    [Fact]
    public void PipelineSteps_MarksOnlyTheSelectedStep()
    {
        var steps = FourSteps();

        var cut = Render<PipelineSteps>(p => p
            .Add(c => c.Diagnostics, steps)
            .Add(c => c.Selected, steps[2]));

        var buttons = cut.FindAll("button.step");
        Assert.Equal(new[] { "false", "false", "true", "false" }, buttons.Select(b => b.GetAttribute("aria-pressed")));
        Assert.Single(buttons, b => b.ClassList.Contains("selected"));
        Assert.Contains("selected", buttons[2].ClassList);
    }

    [Fact]
    public void PipelineSteps_ClickingAStep_RaisesOnStepSelectedWithThatStep()
    {
        var steps = FourSteps();
        PipelineStepDiagnostic? clicked = null;

        var cut = Render<PipelineSteps>(p => p
            .Add(c => c.Diagnostics, steps)
            .Add(c => c.OnStepSelected, step => clicked = step));

        cut.FindAll("button.step")[1].Click();

        Assert.Same(steps[1], clicked);
    }

    [Fact]
    public void PipelineSteps_EmptyList_RendersAnEmptyList()
    {
        var cut = Render<PipelineSteps>(p => p.Add(c => c.Diagnostics, ImmutableList<PipelineStepDiagnostic>.Empty));

        Assert.Empty(cut.FindAll("li"));
        Assert.NotNull(cut.Find("ol.steps"));
    }

    // ----- MaybeView -----

    [Fact]
    public void MaybeView_Something_RendersTheSomethingTemplateWithTheValue()
    {
        var cut = Render<MaybeView<string>>(p => p
            .Add(c => c.State, new Something<string>("LONDON → PARIS"))
            .Add(c => c.SomethingTemplate, value => $"<span class=\"route\">{value}</span>"));

        Assert.Equal("LONDON → PARIS", cut.Find("span.route").TextContent);
        Assert.Empty(cut.FindAll(".alert"));
    }

    [Fact]
    public void MaybeView_Nothing_WithoutTemplate_ShowsTheDefaultMessage()
    {
        var cut = Render<MaybeView<string>>(p => p.Add(c => c.State, new Nothing<string>()));

        var alert = cut.Find(".alert.warn");
        Assert.Contains("No result", alert.TextContent);
        Assert.Contains("Nothing matched the request.", alert.TextContent);
    }

    [Fact]
    public void MaybeView_Nothing_WithTemplate_UsesTheTemplateInstead()
    {
        var cut = Render<MaybeView<string>>(p => p
            .Add(c => c.State, new UnhandledNothing<string>())
            .Add(c => c.NothingTemplate, "<p class=\"custom\">No trains today</p>"));

        Assert.Equal("No trains today", cut.Find("p.custom").TextContent);
        Assert.Empty(cut.FindAll(".alert"));
    }

    [Fact]
    public void MaybeView_NoStateYet_ShowsPlaceholder()
    {
        var cut = Render<MaybeView<string>>(p => p.Add(c => c.State, (Maybe<string>?)null));

        Assert.Equal("No data yet.", cut.Find("p.muted").TextContent);
    }

    [Fact]
    public void MaybeView_Error_ShowsTheExceptionMessage_AndNoRetryForPassengers()
    {
        var retried = 0;

        var cut = Render<MaybeView<string>>(p => p
            .Add(c => c.State, new Error<string>(new TimeoutException("Signal box did not answer.")))
            .Add(c => c.OnRetry, () => retried++)
            .AddCascadingValue(UserRole.Passenger));

        var alert = cut.Find(".alert.err");
        Assert.Equal("alert", alert.GetAttribute("role"));
        Assert.Contains("Signal box did not answer.", alert.TextContent);
        Assert.Contains("Please try again later.", alert.TextContent);
        Assert.Empty(cut.FindAll("button"));
        Assert.Equal(0, retried);
    }

    [Fact]
    public void MaybeView_Error_ForDispatcherWithOnRetry_ShowsRetryButtonThatCallsBack()
    {
        var retried = 0;

        var cut = Render<MaybeView<string>>(p => p
            .Add(c => c.State, new UnhandledError<string>(new InvalidOperationException("Simulated outage")))
            .Add(c => c.OnRetry, () => retried++)
            .AddCascadingValue(UserRole.Dispatcher));

        var button = cut.Find("button");
        Assert.Equal("Clear simulated failures and retry", button.TextContent);
        Assert.DoesNotContain("Please try again later.", cut.Markup);

        button.Click();

        Assert.Equal(1, retried);
    }

    [Fact]
    public void MaybeView_Error_ForDispatcherWithoutOnRetry_HasNoButton()
    {
        var cut = Render<MaybeView<string>>(p => p
            .Add(c => c.State, new Error<string>(new Exception("Broken")))
            .AddCascadingValue(UserRole.Dispatcher));

        Assert.Empty(cut.FindAll("button"));
        Assert.Contains("Please try again later.", cut.Markup);
    }

    [Fact]
    public void MaybeView_Error_WithErrorTemplate_RendersTheTemplate()
    {
        var cut = Render<MaybeView<string>>(p => p
            .Add(c => c.State, new Error<string>(new Exception("Storage offline")))
            .Add(c => c.ErrorTemplate, ex => $"<em class=\"custom-error\">{ex.Message}</em>"));

        Assert.Equal("Storage offline", cut.Find("em.custom-error").TextContent);
        Assert.Empty(cut.FindAll(".alert"));
    }

    [Fact]
    public void MaybeView_ReRendersWhenTheStateChanges()
    {
        var cut = Render<MaybeView<int>>(p => p
            .Add(c => c.State, new Nothing<int>())
            .Add(c => c.SomethingTemplate, n => $"<b>{n} routes</b>"));

        Assert.NotNull(cut.Find(".alert.warn"));

        cut.Render(p => p.Add(c => c.State, new Something<int>(3)));

        Assert.Equal("3 routes", cut.Find("b").TextContent);
        Assert.Empty(cut.FindAll(".alert"));
    }
}
