import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@shared/api/client'
import type {
  CreateEngagementRequest, CreationOptions, EngagementDetail, EngagementList, EngagementRole,
  EngagementStatus, ListScope, Permissions, UserSummary,
} from '@shared/api/types'

export interface ListFilters {
  scope: ListScope
  status: EngagementStatus | null
  search: string
}

export function useEngagements(filters: ListFilters) {
  const params = new URLSearchParams({ scope: filters.scope })
  if (filters.status) params.set('status', filters.status)
  if (filters.search.trim()) params.set('q', filters.search.trim())

  return useQuery({
    queryKey: ['engagements', filters],
    queryFn: () => api<EngagementList>(`/engagements?${params}`),
    placeholderData: (previous) => previous,
  })
}

export const usePermissions = () => useQuery({ queryKey: ['permissions'], queryFn: () => api<Permissions>('/auth/permissions') })

export const useCreationOptions = (enabled: boolean) =>
  useQuery({ queryKey: ['creation-options'], queryFn: () => api<CreationOptions>('/engagements/creation-options'), enabled })

export const useUsers = (enabled: boolean, homeRole?: EngagementRole) =>
  useQuery({
    queryKey: ['users', homeRole ?? 'all'],
    queryFn: () => api<UserSummary[]>(`/auth/users${homeRole ? `?homeRole=${homeRole}` : ''}`),
    enabled,
  })

export function useCreateEngagement() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: CreateEngagementRequest) => api<EngagementDetail>('/engagements', { body: request }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['engagements'] }),
  })
}
