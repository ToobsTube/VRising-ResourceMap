using System;
using UnityEngine;

namespace ResourceMap;

// Waits for the server world to finish loading, then triggers dumps on the configured schedule.
public class DumpRunner : MonoBehaviour
{
    const float PollSeconds = 5f;

    float _nextPoll;
    float _nextDump = -1f;
    float _lastDump;
    int _scheduledRepeat;
    bool _dumpedOnce;
    bool _welcomeWarned;

    public DumpRunner(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        float now = Time.realtimeSinceStartup;

        // A running dump advances every frame, a few milliseconds at a time.
        NodeDumper.Tick();

        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;

        if (_nextDump >= 0f)   // world is ready
        {
            try { Welcome.Poll(now); }
            catch (Exception e)
            {
                if (!_welcomeWarned) Plugin.Logger.LogWarning($"Welcome message failed (further failures not logged): {e}");
                _welcomeWarned = true;
            }
        }

        try
        {
            if (_nextDump < 0f)
            {
                if (!NodeDumper.IsWorldReady()) return;
                _nextDump = now + Math.Max(0, Plugin.DumpDelaySeconds.Value);
                Plugin.Logger.LogInfo($"Server world is ready. Dumping resource nodes in {Plugin.DumpDelaySeconds.Value}s.");
                return;
            }

            // RepeatMinutes changed (.rmap reload): reschedule from the last dump instead of waiting for a restart.
            if (_dumpedOnce && Plugin.RepeatMinutes.Value != _scheduledRepeat) Schedule(_lastDump);

            if (now < _nextDump) return;

            // If an admin's .rmap dump is still running, that one counts as this scheduled dump.
            NodeDumper.TryStart(null);
            _dumpedOnce = true;
            Schedule(now);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Resource dump failed: {e}");
            // Don't retry in a tight loop after a failure; wait for the next scheduled dump (or give up if there is none).
            int repeat = Plugin.RepeatMinutes.Value;
            _nextDump = repeat > 0 || !_dumpedOnce ? now + Math.Max(repeat, 5) * 60f : float.MaxValue;
        }
    }

    void Schedule(float lastDump)
    {
        _lastDump = lastDump;
        _scheduledRepeat = Plugin.RepeatMinutes.Value;
        _nextDump = _scheduledRepeat > 0 ? lastDump + _scheduledRepeat * 60f : float.MaxValue;
    }
}
