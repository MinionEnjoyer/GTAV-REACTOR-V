export type AnimationFrameRequester = (callback: FrameRequestCallback) => number

function nextAnimationFrame(requestFrame: AnimationFrameRequester): Promise<void> {
  return new Promise<void>((resolve) => {
    requestFrame(() => resolve())
  })
}

function nextAnimationFrameWithin(
  requestFrame: AnimationFrameRequester,
  timeoutMs: number,
): Promise<void> {
  if (!Number.isFinite(timeoutMs) || timeoutMs < 0) {
    throw new RangeError('The host-surface frame timeout must be non-negative.')
  }
  return new Promise<void>((resolve) => {
    let settled = false
    const finish = () => {
      if (settled) return
      settled = true
      globalThis.clearTimeout(timer)
      resolve()
    }
    const timer = globalThis.setTimeout(finish, timeoutMs)
    requestFrame(() => finish())
  })
}

function settleAssetsWithin(
  assetsReady: Promise<unknown>,
  timeoutMs: number,
): Promise<void> {
  if (!Number.isFinite(timeoutMs) || timeoutMs < 0) {
    throw new RangeError('The host-surface asset timeout must be non-negative.')
  }

  return new Promise<void>((resolve) => {
    let settled = false
    const finish = () => {
      if (settled) return
      settled = true
      globalThis.clearTimeout(timer)
      resolve()
    }
    const timer = globalThis.setTimeout(finish, timeoutMs)
    void assetsReady.then(finish, finish)
  })
}

/**
 * Bound decorative asset/font loading, then cross two real animation-frame
 * boundaries. React layout effects run before paint; the second frame makes
 * the host acknowledgement describe committed browser pixels rather than only
 * a mutated DOM tree.
 */
export async function waitForHostSurfacePaint(
  assetsReady: Promise<unknown>,
  requestFrame: AnimationFrameRequester,
  assetTimeoutMs = 250,
): Promise<void> {
  await settleAssetsWithin(assetsReady, assetTimeoutMs)
  await nextAnimationFrame(requestFrame)
  await nextAnimationFrame(requestFrame)
}

/**
 * A hidden WebView may throttle requestAnimationFrame indefinitely. Bootstrap
 * readiness therefore uses bounded frame waits, then delegates the real
 * fail-closed qualification to the native full-size pixel probe. Typed menu
 * presentation continues to use waitForHostSurfacePaint's strict frames.
 */
export async function waitForBootstrapHostSurfacePaint(
  assetsReady: Promise<unknown>,
  requestFrame: AnimationFrameRequester,
  assetTimeoutMs = 250,
  frameTimeoutMs = 100,
): Promise<void> {
  await settleAssetsWithin(assetsReady, assetTimeoutMs)
  await nextAnimationFrameWithin(requestFrame, frameTimeoutMs)
  await nextAnimationFrameWithin(requestFrame, frameTimeoutMs)
}
