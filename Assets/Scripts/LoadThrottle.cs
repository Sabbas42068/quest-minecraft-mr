using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A global gatekeeper that limits how many chunk requests are in flight at
/// once, and releases them NEAREST-FIRST.
///
/// The problem it solves: on startup every chunk wants to fetch immediately.
/// Hundreds of simultaneous HTTP requests overwhelm the GDMC mod and the
/// network, so many time out and leave blank spots that only fill in slowly on
/// later poll cycles.
///
/// With this, chunks ask permission before fetching. Only 'maxConcurrent'
/// requests run at a time; the rest wait in a queue ordered by distance from
/// the player. The world loads as an expanding ring from the player outward —
/// which looks intentional, and nothing gets dropped.
/// </summary>
public class LoadThrottle : MonoBehaviour
{
    public static LoadThrottle Instance { get; private set; }

    [Tooltip("How many chunk requests may be in flight at once. Higher = faster " +
             "load but more risk of overwhelming the server. 4-8 is a good range.")]
    public int maxConcurrent = 6;

    private int inFlight = 0;

    // Chunks waiting for a slot, each with a function returning its current
    // distance-from-player priority (smaller = load sooner).
    private class Waiter
    {
        public bool isInitialLoad;
        public System.Func<float> priority;
        public System.Action grant;
    }
    private List<Waiter> queue = new List<Waiter>();

    void Awake()
    {
        Instance = this;
    }

    /// <summary>
    /// A chunk calls this to request a slot.
    /// 'isInitialLoad' = true means this chunk has never loaded yet; those get
    /// absolute priority over re-polls, so the initial world load always
    /// completes before polling traffic can starve distant chunks. Within the
    /// same tier, lower 'priority' (nearer the player) goes first.
    /// </summary>
    public void Request(bool isInitialLoad, System.Func<float> priority, System.Action onGranted)
    {
        queue.Add(new Waiter { isInitialLoad = isInitialLoad, priority = priority, grant = onGranted });
    }

    /// A chunk calls this when its request finishes (success OR failure), to
    /// free its slot for the next waiter.
    public void Release()
    {
        inFlight = Mathf.Max(0, inFlight - 1);
    }

    void Update()
    {
        // Fill available slots. Selection is two-tier:
        //   1. Initial loads (never-loaded chunks) always beat re-polls, so the
        //      first full world load can't be starved by nearby polling traffic.
        //   2. Within the same tier, nearest-to-player goes first.
        while (inFlight < maxConcurrent && queue.Count > 0)
        {
            int best = 0;
            bool bestInit = queue[0].isInitialLoad;
            float bestP = queue[0].priority();

            for (int i = 1; i < queue.Count; i++)
            {
                bool init = queue[i].isInitialLoad;
                float p = queue[i].priority();

                // Prefer initial loads; among equals, prefer nearer.
                bool better = (init && !bestInit)
                           || (init == bestInit && p < bestP);
                if (better)
                {
                    best = i; bestInit = init; bestP = p;
                }
            }

            Waiter w = queue[best];
            queue.RemoveAt(best);
            inFlight++;
            w.grant();
        }
    }
}