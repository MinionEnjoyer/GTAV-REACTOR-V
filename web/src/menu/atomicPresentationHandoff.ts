import type { MenuPresentation } from './presentation'
import type { MenuControllerSnapshot } from './controller'

export interface AtomicPresentationLayers {
  visible: MenuPresentation | null
  preparing: MenuPresentation | null
}

/**
 * Keep the last host-accepted tree on screen while the requested replacement
 * loads inertly. A request is not visible merely because its event arrived;
 * only the exact accepted presentation may replace the committed frame.
 */
export function resolveAtomicPresentationLayers(
  requested: MenuPresentation | null,
  committed: MenuPresentation | null,
): AtomicPresentationLayers {
  const preparing = requested !== null &&
    requested.presentationId !== committed?.presentationId
    ? requested
    : null
  return { visible: committed, preparing }
}

/** Ignore stale/rejected acknowledgements and commit only the current request. */
export function commitAcceptedPresentation(
  requested: MenuPresentation | null,
  committed: MenuPresentation | null,
  acceptedPresentationId: string,
): MenuPresentation | null {
  return requested?.presentationId === acceptedPresentationId
    ? requested
    : committed
}

/**
 * Carry navigation state only between revisions of the same authoritative
 * provider menu. Atomic replacement uses two independently keyed React trees,
 * so the preparing tree cannot read the committed tree's controller ref.
 */
export function selectReplacementRestoreSnapshot(
  requested: MenuPresentation | null,
  committed: MenuPresentation | null,
  committedSnapshot: MenuControllerSnapshot | null | undefined,
): MenuControllerSnapshot | null {
  if (!requested || !committed || !committedSnapshot) return null
  if (requested.extensionId !== committed.extensionId || requested.menuId !== committed.menuId) {
    return null
  }
  return committedSnapshot
}
