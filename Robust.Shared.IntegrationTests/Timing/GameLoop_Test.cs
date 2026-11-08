using System;
using System.Collections.Generic; // CMU14
using System.Reflection;
using Moq;
using NUnit.Framework;
using Robust.Shared.Exceptions;
using Robust.Shared.Log;
using Robust.Shared.Profiling;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Shared.Timing
{
    [TestFixture]
    [TestOf(typeof(GameLoop))]
    sealed class GameLoop_Test : OurRobustUnitTest
    {
        /// <summary>
        ///     With single step enabled, the game loop should run 1 tick and then pause again.
        /// </summary>
        [Test]
        public void SingleStepTest()
        {
            // TimeoutAttribute causes this to run on different thread on .NET Core,
            // which messes up IoC if we don't run this:
            BaseSetup();
            // Arrange
            var elapsedVal = TimeSpan.FromSeconds(Math.PI);
            var newStopwatch = new Mock<IStopwatch>();
            newStopwatch.SetupGet(p => p.Elapsed).Returns(elapsedVal);
            var gameTiming = GameTimingFactory(newStopwatch.Object);
            gameTiming.Paused = false;
            var loop = new GameLoop(
                gameTiming,
                new RuntimeLog(),
                new ProfManager(),
                new LogManager().RootSawmill,
                new GameLoopOptions(false));

            var callCount = 0;
            loop.BeforeCatchUpTick += () => Assert.Fail("Paused single-step ticks must not pump input."); // CMU14
            loop.Tick += (sender, args) => callCount++;
            loop.Render += (sender, args) => loop.Running = false; // break the endless loop for testing

            // Act
            loop.SingleStep = true;
            loop.Run();

            // Assert
            Assert.That(callCount, Is.EqualTo(1));
            Assert.That(gameTiming.CurTick, Is.EqualTo(new GameTick(2)));
            Assert.That(gameTiming.Paused, Is.True); // it will pause itself after running each tick
            Assert.That(loop.SingleStep, Is.True); // still true
        }

        // CMU14 Begin: incoming movement must not wait behind an entire catch-up frame.
        [TestCase(true)]
        [TestCase(false)]
        public void CatchUpTicksObserveNewInputWhenServerPumpIsSubscribed(bool serverPump)
        {
            BaseSetup();
            var stopwatch = new Mock<IStopwatch>();
            stopwatch.SetupGet(p => p.Elapsed).Returns(TimeSpan.FromSeconds(Math.PI));
            var timing = GameTimingFactory(stopwatch.Object);
            timing.Paused = false;
            var loop = new GameLoop(timing, new RuntimeLog(), new ProfManager(),
                new LogManager().RootSawmill, new GameLoopOptions(false));
            var incoming = new Queue<int>();
            var simulatedInputs = new List<int>();
            var receivedOutsideSimulation = new List<bool>();
            var direction = 0;
            var frameInputs = 0;
            loop.Input += (_, _) => frameInputs++;
            if (serverPump)
            {
                loop.BeforeCatchUpTick += () =>
                {
                    receivedOutsideSimulation.Add(!timing.InSimulation);
                    while (incoming.TryDequeue(out var input))
                        direction = input;
                };
            }
            loop.Tick += (_, _) =>
            {
                Assert.That(timing.InSimulation, Is.True);
                simulatedInputs.Add(direction);
                if (simulatedInputs.Count == 1)
                    incoming.Enqueue(1);
            };
            loop.Render += (_, _) => loop.Running = false;

            loop.Run();

            Assert.That(simulatedInputs, Is.EqualTo(serverPump
                ? new[] { 0, 1, 1, 1, 1 }
                : new[] { 0, 0, 0, 0, 0 }));
            Assert.That(frameInputs, Is.EqualTo(1), "Frame callbacks must keep their existing cadence.");
            Assert.That(receivedOutsideSimulation, Is.EqualTo(serverPump
                ? new[] { true, true, true, true }
                : Array.Empty<bool>()));
        }
        // CMU14 End

        private static IGameTiming GameTimingFactory(IStopwatch stopwatch)
        {
            var timing = new GameTiming();

            var field = typeof(GameTiming).GetField("_realTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(timing, stopwatch);

            return timing;
        }
    }
}
