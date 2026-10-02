using Lemmix.Util;

namespace Lemmix.Tests.Util;

public class JsMathTests
{
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 3)]
    [InlineData(-0.5, 0)]
    [InlineData(-1.5, -1)]
    [InlineData(-2.5, -2)]
    [InlineData(127.49999, 127)]
    public void RoundMatchesJavaScript(double x, double expected) => Assert.Equal(expected, JsMath.Round(x));

    [Theory]
    [InlineData(3.9, 3)]
    [InlineData(-3.9, -3)]
    [InlineData(4294967296.0 + 5, 5)]
    [InlineData(2147483648.0, -2147483648)]
    [InlineData(double.NaN, 0)]
    public void ToInt32MatchesBitwiseOr(double x, int expected) => Assert.Equal(expected, JsMath.ToInt32(x));

    [Fact]
    public void ToUint32MatchesUnsignedShift() => Assert.Equal(4294967295u, JsMath.ToUint32(-1));
}
