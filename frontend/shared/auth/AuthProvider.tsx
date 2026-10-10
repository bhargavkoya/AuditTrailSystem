import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, configureApi } from '../api/client'
import type { CurrentUser, LoginResponse } from '../api/types'

const STORAGE_KEY = 'auditflow.session'

interface Session {
  token: string
  expiresAt: string
  user: CurrentUser
}

// sessionStorage survives a refresh; it can throw (blocked storage), so everything falls back to memory.
let memorySession: Session | null = null

function readSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY)
    const session = raw ? (JSON.parse(raw) as Session) : memorySession
    return session && new Date(session.expiresAt) > new Date() ? session : null
  } catch {
    return memorySession
  }
}

function writeSession(session: Session | null) {
  memorySession = session
  try {
    if (session) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session))
    else sessionStorage.removeItem(STORAGE_KEY)
  } catch {
    /* storage unavailable: memory only */
  }
}

interface AuthContextValue {
  user: CurrentUser | null
  login: (email: string, password: string) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => readSession())
  const queryClient = useQueryClient()

  const logout = useCallback(() => {
    writeSession(null)
    setSession(null)
    queryClient.clear()
  }, [queryClient])

  // Keep the API client pointed at the current token synchronously, before children fetch.
  configureApi({ getToken: () => session?.token ?? null, onUnauthorized: logout })

  useEffect(() => {
    if (!session) return
    const remaining = new Date(session.expiresAt).getTime() - Date.now()
    const timer = setTimeout(logout, Math.max(remaining, 0))
    return () => clearTimeout(timer)
  }, [session, logout])

  const login = useCallback(async (email: string, password: string) => {
    const response = await api<LoginResponse>('/auth/login', { body: { email, password } })
    const next = { token: response.accessToken, expiresAt: response.expiresAt, user: response.user }
    writeSession(next)
    setSession(next)
  }, [])

  const value = useMemo(() => ({ user: session?.user ?? null, login, logout }), [session, login, logout])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}
