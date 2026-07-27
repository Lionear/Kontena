using System.Globalization;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class LocalClusterNameTests
{
    [Theory]
    [InlineData("dev")]
    [InlineData("kind")]
    [InlineData("team-a.dev")]
    [InlineData("k8s-1-31")]
    public void Names_a_container_and_a_context_can_both_carry_are_accepted(string name)
    {
        Assert.True(LocalClusterName.IsValid(name));
        Assert.Null(LocalClusterName.Problem(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Dev")]
    [InlineData("dev cluster")]
    [InlineData("-dev")]
    [InlineData("dev-")]
    [InlineData("dev_cluster")]
    public void Names_that_would_fail_halfway_through_a_create_are_refused(string name)
    {
        Assert.False(LocalClusterName.IsValid(name));
        Assert.NotNull(LocalClusterName.Problem(name));
    }

    [Fact]
    public void A_null_name_is_a_problem_rather_than_an_exception()
    {
        Assert.NotNull(LocalClusterName.Problem(null));
    }

    [Fact]
    public void Too_long_is_refused_and_says_how_long_is_allowed()
    {
        var problem = LocalClusterName.Problem(new string('a', LocalClusterName.MaxLength + 1));

        Assert.Contains(
            LocalClusterName.MaxLength.ToString(CultureInfo.InvariantCulture),
            problem,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_reason_says_which_rule_was_broken()
    {
        Assert.Contains("lowercase", LocalClusterName.Problem("Dev"), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_throws_naming_the_parameter_it_was_given()
    {
        var error = Assert.Throws<ArgumentException>(() => LocalClusterName.Validate("Dev", "spec"));

        Assert.Equal("spec", error.ParamName);
    }
}
