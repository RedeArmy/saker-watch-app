using AwesomeAssertions;

namespace SakerWatch.Integration.Tests.Infrastructure;

public sealed class MongoFixtureTests
{
    [Fact]
    public void CpuHasAvx_AnX86CpuThatListsAvx_IsTrue() =>
        MongoFixture.CpuHasAvx(["processor\t: 0", "flags\t\t: fpu vme sse4_2 avx avx2"]).Should().BeTrue();

    [Fact]
    public void CpuHasAvx_AnX86CpuWithoutAvx_IsFalse() =>
        MongoFixture.CpuHasAvx(["processor\t: 0", "flags\t\t: fpu vme sse4_2 popcnt"]).Should().BeFalse();

    [Fact]
    public void CpuHasAvx_AnArm64CpuThatHasNoFlagsLine_IsTrue_BecauseItIsNotWhatTheCrashIsAbout() =>
        MongoFixture.CpuHasAvx(["processor\t: 0", "BogoMIPS\t: 50.00", "Features\t: fp asimd evtstrm aes pmull sha1 sha2 crc32"]).Should().BeTrue();

    [Fact]
    public void CpuHasAvx_AnAvx512OnlyListing_IsNotMistakenForAvx() =>
        MongoFixture.CpuHasAvx(["flags\t\t: fpu vme xavx512 noavx"]).Should().BeFalse();
}
