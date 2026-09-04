import { describe, expect, it } from 'vitest'
import {
  waitForBootstrapHostSurfacePaint,
  waitForHostSurfacePaint,
} from './hostSurfacePaint'

describe('host surface paint boundary', () => {
  it('waits for assets and fonts before crossing two animation frames', async () => {
    const order: string[] = []
    const assetsReady = Promise.resolve().then(() => { order.push('assets-ready') })
    const requestFrame = (callback: FrameRequestCallback) => {
      order.push('animation-frame')
      callback(0)
      return 1
    }

    await waitForHostSurfacePaint(assetsReady, requestFrame, 250)

    expect(order).toEqual([
      'assets-ready',
      'animation-frame',
      'animation-frame',
    ])
  })

  it('keeps the asset wait bounded while retaining both frame boundaries', async () => {
    let frames = 0
    await waitForHostSurfacePaint(
      new Promise(() => {}),
      (callback) => {
        frames += 1
        callback(0)
        return frames
      },
      1,
    )

    expect(frames).toBe(2)
  })

  it('rejects an invalid timeout instead of silently changing readiness', async () => {
    await expect(waitForHostSurfacePaint(
      Promise.resolve(),
      () => 0,
      -1,
    )).rejects.toThrow(/non-negative/)
  })

  it('bounds bootstrap frame waits when a hidden browser throttles rAF', async () => {
    const started = performance.now()
    await waitForBootstrapHostSurfacePaint(
      Promise.resolve(),
      () => 1,
      1,
      2,
    )

    expect(performance.now() - started).toBeLessThan(100)
  })
})
