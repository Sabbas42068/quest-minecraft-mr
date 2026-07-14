using UnityEngine;

/// <summary>
/// Motion smoothing for a live entity marker (player or mob).
///
/// The problem: we get a position every 100-400ms, but render every ~14ms. So
/// most frames have NO new data. Naive Lerp-toward-target looks smooth but
/// always LAGS behind the true position, which feels rubbery.
///
/// The fix (what multiplayer games do): estimate velocity from consecutive
/// updates and EXTRAPOLATE forward between them, so the marker keeps moving at
/// the right speed in the right direction and arrives roughly where the entity
/// actually is. Corrections are then blended in smoothly rather than snapped.
/// </summary>
public class SmoothedMarker
{
    private Vector3 renderedPos;      // what we actually draw
    private Vector3 lastKnownPos;     // most recent server position
    private Vector3 velocity;         // estimated, in map-local units/sec

    private Quaternion renderedRot;
    private Quaternion targetRot;

    private float lastUpdateTime = -1f;
    private bool initialised = false;

    /// How aggressively the rendered position is pulled toward the predicted
    /// one. Higher = tighter tracking but more visible correction snaps.
    public float correctionSpeed = 12f;

    /// How fast rotation catches up.
    public float rotationSpeed = 10f;

    /// Cap on extrapolation, so a stalled connection doesn't send the marker
    /// flying off across the map on a stale velocity.
    public float maxExtrapolationSeconds = 0.5f;

    /// Called each time a fresh position arrives from the server.
    public void PushUpdate(Vector3 newPos, Quaternion newRot)
    {
        float now = Time.time;

        if (!initialised)
        {
            // First sighting: just place it, no motion to infer yet.
            renderedPos = newPos;
            lastKnownPos = newPos;
            renderedRot = newRot;
            targetRot = newRot;
            velocity = Vector3.zero;
            lastUpdateTime = now;
            initialised = true;
            return;
        }

        float dt = now - lastUpdateTime;
        if (dt > 0.0001f)
        {
            // Estimate velocity from how far it moved since the last update.
            Vector3 newVelocity = (newPos - lastKnownPos) / dt;

            // Blend with the previous estimate to damp out jitter from a single
            // noisy sample (a lone bad reading shouldn't fling the marker).
            velocity = Vector3.Lerp(velocity, newVelocity, 0.6f);
        }

        lastKnownPos = newPos;
        targetRot = newRot;
        lastUpdateTime = now;
    }

    /// Called every frame to advance the marker.
    public void Tick(Transform t)
    {
        if (!initialised) return;

        float sinceUpdate = Mathf.Min(Time.time - lastUpdateTime, maxExtrapolationSeconds);

        // Where we THINK it is right now: last known position, projected
        // forward along its estimated velocity.
        Vector3 predicted = lastKnownPos + velocity * sinceUpdate;

        // Ease the rendered position toward the prediction. This absorbs
        // correction errors smoothly instead of snapping when we're wrong.
        renderedPos = Vector3.Lerp(renderedPos, predicted,
                                   1f - Mathf.Exp(-correctionSpeed * Time.deltaTime));

        renderedRot = Quaternion.Slerp(renderedRot, targetRot,
                                       1f - Mathf.Exp(-rotationSpeed * Time.deltaTime));

        t.localPosition = renderedPos;
        t.localRotation = renderedRot;
    }

    /// Snap instantly (used when smoothing is disabled).
    public void SnapTo(Transform t, Vector3 pos, Quaternion rot)
    {
        renderedPos = lastKnownPos = pos;
        renderedRot = targetRot = rot;
        initialised = true;
        lastUpdateTime = Time.time;
        t.localPosition = pos;
        t.localRotation = rot;
    }
}
