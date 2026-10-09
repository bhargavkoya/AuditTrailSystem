import { useCallback, useEffect, useState } from 'react'

export const SERVICES = ['auth', 'engagement', 'workflow', 'attachment', 'review', 'notification'] as const
export type ServiceName = (typeof SERVICES)[number]

export interface HealthCheck {
  name: string
  status: string
  error?: string | null
}

export interface ServiceHealth {
  service: ServiceName
  status: 'Healthy' | 'Degraded' | 'Unhealthy' | 'Unreachable' | 'Checking'
  checks: HealthCheck[]
}

async function fetchHealth(service: ServiceName): Promise<ServiceHealth> {
  try {
    const res = await fetch(`/health/${service}`, { headers: { Accept: 'application/json' } })
    const body = (await res.json()) as { status: ServiceHealth['status']; checks?: HealthCheck[] }
    return { service, status: body.status, checks: body.checks ?? [] }
  } catch {
    return { service, status: 'Unreachable', checks: [] }
  }
}

export function useServiceHealth(intervalMs = 10_000) {
  const [results, setResults] = useState<ServiceHealth[]>(
    SERVICES.map((service) => ({ service, status: 'Checking', checks: [] })),
  )

  const refresh = useCallback(async () => {
    setResults(await Promise.all(SERVICES.map(fetchHealth)))
  }, [])

  useEffect(() => {
    void refresh()
    const id = setInterval(() => void refresh(), intervalMs)
    return () => clearInterval(id)
  }, [refresh, intervalMs])

  return { results, refresh }
}
