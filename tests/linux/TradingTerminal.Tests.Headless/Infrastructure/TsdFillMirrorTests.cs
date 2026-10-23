using FluentAssertions;
using TradingTerminal.Infrastructure.TsdBridge;
using Xunit;

namespace TradingTerminal.Tests.Headless.Infrastructure;

public sealed class TsdFillMirrorTests
{
    [Fact]
    public async Task Full_fill_posts_real_price_and_quantity_once()
    {
        var poster = new RecordingPoster();
        var mirror = new TsdFillMirror(poster);
        mirror.TryRegister(Binding("ord-1"), paper: true).Should().BeTrue();

        var posted = await mirror.ObserveAsync(Fill("ord-1", 4m, 191.25m, "fill-1", 4m), CancellationToken.None);

        posted.Should().BeTrue();
        poster.Posts.Should().ContainSingle();
        var fill = poster.Posts[0];
        fill.ClientOrderId.Should().Be("ord-1");
        fill.Quantity.Should().Be(4d);
        fill.Price.Should().Be(191.25d);
        fill.BookId.Should().Be("book-1");
        fill.IntentVersion.Should().Be("sv-9");
        fill.DedupeKey.Should().Be("dedupe-1:fill-1");
        fill.SignalId.Should().Be("sig-1");
        fill.DaxOrderId.Should().Be("dax:sig-1");
    }

    [Fact]
    public async Task Partial_fills_post_incremental_slices_with_distinct_dedupe_keys()
    {
        var poster = new RecordingPoster();
        var mirror = new TsdFillMirror(poster);
        mirror.TryRegister(Binding("ord-1"), paper: true);

        (await mirror.ObserveAsync(Fill("ord-1", 2m, 190m, "fill-1", 2m), CancellationToken.None)).Should().BeTrue();
        (await mirror.ObserveAsync(Fill("ord-1", 3m, 191m, "fill-2", 5m), CancellationToken.None)).Should().BeTrue();

        poster.Posts.Select(item => item.Quantity).Should().Equal(2d, 3d);
        poster.Posts.Select(item => item.Price).Should().Equal(190d, 191d);
        poster.Posts.Select(item => item.DedupeKey).Should().Equal("dedupe-1:fill-1", "dedupe-1:fill-2");
        poster.Posts.Should().OnlyContain(item => item.ClientOrderId == "ord-1");
    }

    [Fact]
    public async Task Replayed_fill_key_does_not_post_again()
    {
        var poster = new RecordingPoster();
        var mirror = new TsdFillMirror(poster);
        mirror.TryRegister(Binding("ord-1"), paper: true);

        await mirror.ObserveAsync(Fill("ord-1", 2m, 190m, "fill-1", 2m), CancellationToken.None);
        var replay = await mirror.ObserveAsync(Fill("ord-1", 2m, 190m, "fill-1", 2m), CancellationToken.None);

        replay.Should().BeFalse();
        poster.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task Cumulative_catch_up_posts_only_the_unmirrored_gap_and_replays_stay_quiet()
    {
        var poster = new RecordingPoster();
        var mirror = new TsdFillMirror(poster);
        mirror.TryRegister(Binding("ord-1"), paper: true);

        await mirror.ObserveAsync(Fill("ord-1", 2m, 190m, "fill-1", 2m), CancellationToken.None);
        var caughtUp = await mirror.ObserveAsync(Fill("ord-1", 5m, 190m, "fill-reconnect", 5m), CancellationToken.None);
        var replayGap = await mirror.ObserveAsync(Fill("ord-1", 5m, 190m, "fill-reconnect", 5m), CancellationToken.None);
        var replayFirst = await mirror.ObserveAsync(Fill("ord-1", 2m, 190m, "fill-1", 2m), CancellationToken.None);

        caughtUp.Should().BeTrue();
        replayGap.Should().BeFalse();
        replayFirst.Should().BeFalse();
        poster.Posts.Select(item => item.Quantity).Should().Equal(2d, 3d);
        poster.Posts[1].DedupeKey.Should().Be("dedupe-1:fill-reconnect");
    }

    [Fact]
    public async Task Failed_post_is_retried_by_flush_without_a_second_slice()
    {
        var poster = new RecordingPoster { Succeed = false };
        var mirror = new TsdFillMirror(poster);
        mirror.TryRegister(Binding("ord-1"), paper: true);

        (await mirror.ObserveAsync(Fill("ord-1", 2m, 190m, "fill-1", 2m), CancellationToken.None)).Should().BeFalse();
        poster.Posts.Should().BeEmpty();

        poster.Succeed = true;
        await mirror.FlushAsync(CancellationToken.None);

        poster.Posts.Should().ContainSingle();
        poster.Posts[0].Quantity.Should().Be(2d);
        (await mirror.ObserveAsync(Fill("ord-1", 2m, 190m, "fill-1", 2m), CancellationToken.None)).Should().BeFalse();
        poster.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task Live_registration_and_unknown_orders_do_not_post()
    {
        var poster = new RecordingPoster();
        var mirror = new TsdFillMirror(poster);

        mirror.TryRegister(Binding("ord-live"), paper: false).Should().BeFalse();
        (await mirror.ObserveAsync(Fill("ord-live", 1m, 10m, "fill-1", 1m), CancellationToken.None)).Should().BeFalse();
        (await mirror.ObserveAsync(Fill("ord-missing", 1m, 10m, "fill-1", 1m), CancellationToken.None)).Should().BeFalse();

        poster.Posts.Should().BeEmpty();
    }

    private static TsdFillBinding Binding(string orderId) =>
        new(orderId, "AAPL", "ALPACA_PAPER", "BUY", "sig-1", "dax:sig-1", "book-1", "sv-9", "dedupe-1");

    private static TsdFillObservation Fill(
        string orderId,
        decimal incremental,
        decimal price,
        string key,
        decimal cumulative) =>
        new(orderId, incremental, price, key, cumulative);

    private sealed class RecordingPoster : ITsdFillPoster
    {
        public List<BridgeFillDto> Posts { get; } = [];

        public bool Succeed { get; set; } = true;

        public Task<bool> PostAsync(BridgeFillDto fill, CancellationToken cancellationToken)
        {
            if (!Succeed)
                return Task.FromResult(false);
            Posts.Add(fill);
            return Task.FromResult(true);
        }
    }
}
