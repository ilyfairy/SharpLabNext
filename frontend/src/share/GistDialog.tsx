import { ExternalLink, GitFork, LoaderCircle, LogIn, LogOut, Plus, Save, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { createGist, getGitHubAuthStatus, logoutGitHub, startGitHubOAuth, updateGist } from '../api/client'
import type { GistDocument, GistWorkspaceState, GitHubAuthStatus } from '../api/types'

interface GistDialogProps {
  open: boolean
  workspace: GistWorkspaceState | null
  currentGist: GistDocument | null
  onClose: () => void
  onSaved: (gist: GistDocument) => void
}

export function GistDialog({ open, workspace, currentGist, onClose, onSaved }: GistDialogProps) {
  const closeButton = useRef<HTMLButtonElement | null>(null)
  const [auth, setAuth] = useState<GitHubAuthStatus | null>(null)
  const [description, setDescription] = useState('')
  const [isPublic, setIsPublic] = useState(false)
  const [busy, setBusy] = useState<'auth' | 'create' | 'update' | 'logout' | null>(null)
  const [error, setError] = useState<Error | null>(null)

  useEffect(() => {
    if (!open) return
    setDescription(currentGist?.description ?? '')
    setIsPublic(currentGist?.isPublic ?? false)
    setError(null)
    const controller = new AbortController()
    void getGitHubAuthStatus(controller.signal)
      .then(setAuth)
      .catch((reason: unknown) => {
        if (!controller.signal.aborted) {
          setError(reason instanceof Error ? reason : new Error('GitHub status is unavailable.'))
        }
      })
    closeButton.current?.focus()
    return () => controller.abort()
  }, [currentGist, open])

  useEffect(() => {
    if (!open) return
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && busy === null) onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [busy, onClose, open])

  if (!open) return null

  const signIn = async () => {
    setBusy('auth')
    setError(null)
    try {
      const returnPath = `${window.location.pathname}${window.location.search}${window.location.hash}`
      const response = await startGitHubOAuth(returnPath)
      window.location.assign(response.authorizationUrl)
    } catch (reason) {
      setError(reason instanceof Error ? reason : new Error('GitHub sign-in could not start.'))
      setBusy(null)
    }
  }

  const create = async () => {
    if (!workspace || !auth?.csrfToken) return
    setBusy('create')
    setError(null)
    try {
      onSaved(await createGist({ description: description.trim(), isPublic, workspace }, auth.csrfToken))
    } catch (reason) {
      setError(reason instanceof Error ? reason : new Error('The Gist could not be created.'))
    } finally {
      setBusy(null)
    }
  }

  const update = async () => {
    if (!workspace || !currentGist?.canUpdate || !auth?.csrfToken) return
    setBusy('update')
    setError(null)
    try {
      onSaved(await updateGist(currentGist.id, { description: description.trim(), workspace }, auth.csrfToken))
    } catch (reason) {
      setError(reason instanceof Error ? reason : new Error('The Gist could not be updated.'))
    } finally {
      setBusy(null)
    }
  }

  const logout = async () => {
    if (!auth?.csrfToken) return
    setBusy('logout')
    setError(null)
    try {
      await logoutGitHub(auth.csrfToken)
      setAuth({
        available: auth.available,
        authenticated: false,
        login: null,
        csrfToken: null,
      })
    } catch (reason) {
      setError(reason instanceof Error ? reason : new Error('GitHub sign-out failed.'))
    } finally {
      setBusy(null)
    }
  }

  const authenticated = auth?.authenticated === true
  return (
    <div className="modal-backdrop" role="presentation">
      <section className="gist-dialog" role="dialog" aria-modal="true" aria-labelledby="gist-dialog-title" onMouseDown={(event) => event.stopPropagation()}>
        <header>
          <div>
            <GitFork aria-hidden="true" size={17} />
            <h2 id="gist-dialog-title">GitHub Gist</h2>
          </div>
          <button ref={closeButton} className="icon-button" type="button" title="Close" aria-label="Close Gist dialog" disabled={busy !== null} onClick={onClose}>
            <X aria-hidden="true" size={15} />
          </button>
        </header>

        {currentGist && (
          <div className="gist-current">
            <span>{currentGist.isPublic ? 'Public' : 'Private'}</span>
            <a href={currentGist.htmlUrl} target="_blank" rel="noreferrer">
              {currentGist.id}
              <ExternalLink aria-hidden="true" size={12} />
            </a>
          </div>
        )}

        <label className="gist-field">
          <span>Description</span>
          <input value={description} maxLength={256} disabled={busy !== null} onChange={(event) => setDescription(event.target.value)} />
        </label>

        <label className="gist-visibility">
          <input type="checkbox" checked={isPublic} disabled={busy !== null} onChange={(event) => setIsPublic(event.target.checked)} />
          <span>Public Gist</span>
        </label>

        {error && (
          <div className="gist-error" role="alert">
            {error.message}
          </div>
        )}

        <footer>
          <div className="gist-auth">
            {auth === null ? (
              <LoaderCircle className="spin" aria-label="Loading GitHub status" size={15} />
            ) : authenticated ? (
              <>
                <span>{auth.login}</span>
                <button className="icon-button" type="button" title="Sign out of GitHub" aria-label="Sign out of GitHub" disabled={busy !== null} onClick={() => void logout()}>
                  <LogOut aria-hidden="true" size={14} />
                </button>
              </>
            ) : (
              <button className="secondary-command" type="button" disabled={!auth.available || busy !== null} onClick={() => void signIn()}>
                <LogIn aria-hidden="true" size={14} />
                Sign in
              </button>
            )}
          </div>
          <div className="gist-commands">
            {currentGist?.canUpdate && (
              <button className="secondary-command" type="button" disabled={!workspace || !authenticated || busy !== null} onClick={() => void update()}>
                {busy === 'update' ? <LoaderCircle className="spin" aria-hidden="true" size={14} /> : <Save aria-hidden="true" size={14} />}
                Save changes
              </button>
            )}
            <button className="primary-command" type="button" disabled={!workspace || !authenticated || busy !== null} onClick={() => void create()}>
              {busy === 'create' ? <LoaderCircle className="spin" aria-hidden="true" size={14} /> : <Plus aria-hidden="true" size={14} />}
              New Gist
            </button>
          </div>
        </footer>
      </section>
    </div>
  )
}
