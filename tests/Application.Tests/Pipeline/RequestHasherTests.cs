using System.Text.Json.Nodes;
using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.Application.Tests.Pipeline;

public sealed class RequestHasherTests
{
    private sealed record Body(string Name, decimal Price, string[] Tags);

    private static readonly IdempotencyKeyInfo _key = new("0123456789abcdef-key", "POST", "/v1/things");

    [Fact]
    public void Hash_IsStableForTheSameRequest()
    {
        var body = new Body("NVDA", 123.45m, ["a", "b"]);

        RequestHasher.Hash(body, _key).Should().Equal(RequestHasher.Hash(body, _key));
    }

    [Fact]
    public void Hash_DiffersWhenAnyValueMethodOrRouteDiffers()
    {
        var body = new Body("NVDA", 123.45m, ["a", "b"]);
        byte[] baseline = RequestHasher.Hash(body, _key);

        RequestHasher.Hash(body with { Name = "AMD" }, _key).Should().NotEqual(baseline);
        RequestHasher.Hash(body with { Price = 123.46m }, _key).Should().NotEqual(baseline);
        RequestHasher.Hash(body with { Tags = ["b", "a"] }, _key).Should().NotEqual(baseline, "array order is significant");
        RequestHasher.Hash(body, _key with { Method = "PUT" }).Should().NotEqual(baseline);
        RequestHasher.Hash(body, _key with { Route = "/v1/other" }).Should().NotEqual(baseline);
    }

    [Fact]
    public void Hash_IgnoresTheKeyItself()
    {
        var body = new Body("NVDA", 1m, []);

        RequestHasher.Hash(body, _key with { Key = "another-key-0123456789" }).Should().Equal(RequestHasher.Hash(body, _key));
    }

    [Fact]
    public void Canonical_IsIndependentOfPropertyOrderAndWhitespace_ForRandomDocuments()
    {
        var random = new Random(20261013);
        for (int round = 0; round < 200; round++)
        {
            JsonObject original = RandomObject(random, depth: 3);
            JsonNode? shuffled = Shuffle(original, random);
            var reparsed = JsonNode.Parse(shuffled!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            RequestHasher.Canonical(reparsed).Should().Be(RequestHasher.Canonical(original), $"round {round}");
        }
    }

    [Fact]
    public void Canonical_DistinguishesDifferentDocuments_ForRandomPairs()
    {
        var random = new Random(7);
        for (int round = 0; round < 100; round++)
        {
            JsonObject a = RandomObject(random, depth: 2);
            JsonObject b = RandomObject(random, depth: 2);
            if (a.ToJsonString() == b.ToJsonString())
            {
                continue;
            }

            RequestHasher.Canonical(a).Should().NotBe(RequestHasher.Canonical(b), $"round {round}");
        }
    }

    [Fact]
    public void Snapshot_RoundTripsResultsWithAndWithoutValues()
    {
        var value = new Body("NVDA", 123.45m, ["a"]);

        Result<Body> restored = ResultSnapshot.Restore<Result<Body>>(ResultSnapshot.Serialize(Result.Success(value))!);
        Result plain = ResultSnapshot.Restore<Result>(ResultSnapshot.Serialize(Result.Success())!);

        restored.Value.Name.Should().Be("NVDA");
        restored.Value.Price.Should().Be(123.45m);
        restored.Value.Tags.Should().Equal("a");
        plain.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Snapshot_OfAnOversizedResult_IsNotStored()
    {
        var huge = new Body(new string('x', ResultSnapshot.MaxBodyBytes + 1), 1m, []);

        ResultSnapshot.Serialize(Result.Success(huge)).Should().BeNull();
    }

    [Fact]
    public void Snapshot_OfAFailure_IsAProgrammingError()
    {
        Action act = () => ResultSnapshot.Serialize(Result.Failure<Body>(Error.Conflict("x", "y")));

        act.Should().Throw<InvalidOperationException>();
    }

    private static JsonObject RandomObject(Random random, int depth)
    {
        var obj = new JsonObject();
        int count = random.Next(1, 6);
        for (int i = 0; i < count; i++)
        {
            obj[$"k{random.Next(0, 50)}"] = RandomValue(random, depth);
        }

        return obj;
    }

    private static JsonNode? RandomValue(Random random, int depth) => random.Next(depth > 0 ? 6 : 4) switch
    {
        0 => random.Next(-1000, 1000),
        1 => $"text-{random.Next(0, 1000)}",
        2 => random.Next(2) == 0,
        3 => null,
        4 => RandomObject(random, depth - 1),
        _ => new JsonArray(Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomValue(random, depth - 1)).ToArray()),
    };

    private static JsonNode? Shuffle(JsonNode? node, Random random) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(_ => random.Next()).Select(pair => KeyValuePair.Create(pair.Key, Shuffle(pair.Value, random)))),
        JsonArray array => new JsonArray(array.Select(item => Shuffle(item, random)).ToArray()),
        _ => node?.DeepClone(),
    };
}
