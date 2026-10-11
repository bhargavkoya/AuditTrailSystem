import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, uploadFile } from '@shared/api/client'
import type {
  Attachment, EngagementDetail, Participant, SaveTabResult, SectionPatch, TabsResponse, TabView,
  UpdateEngagementRequest, ValidationSummary,
} from '@shared/api/types'

export const useEngagement = (id: string) =>
  useQuery({ queryKey: ['engagement', id], queryFn: () => api<EngagementDetail>(`/engagements/${id}`), retry: false })

export const useParticipants = (id: string, enabled: boolean) =>
  useQuery({ queryKey: ['participants', id], queryFn: () => api<Participant[]>(`/engagements/${id}/participants`), retry: false, enabled })

/** Polls every 2 s while workflow-service is still provisioning a freshly created engagement. */
export const useTabs = (id: string, enabled: boolean) =>
  useQuery({
    queryKey: ['tabs', id],
    queryFn: () => api<TabsResponse>(`/engagements/${id}/tabs`),
    enabled,
    retry: false,
    refetchInterval: (query) => (query.state.data && !query.state.data.ready ? 2000 : false),
  })

export const useTab = (id: string, tabNo: number, enabled: boolean) =>
  useQuery({ queryKey: ['tab', id, tabNo], queryFn: () => api<TabView>(`/engagements/${id}/tabs/${tabNo}`), enabled, retry: false })

export const useValidationSummary = (id: string, enabled: boolean) =>
  useQuery({
    queryKey: ['validation-summary', id],
    queryFn: () => api<ValidationSummary>(`/engagements/${id}/validation-summary`),
    enabled,
    retry: false,
  })

/** Only requested when the backend granted attachment.view; Viewers never call this endpoint. */
export const useAttachments = (id: string, enabled: boolean) =>
  useQuery({ queryKey: ['attachments', id], queryFn: () => api<Attachment[]>(`/engagements/${id}/attachments`), enabled, retry: false })

/** Status changes (Draft -> In Progress) are applied asynchronously by an event, so refetch the header twice. */
function useRefreshAfterWrite(id: string) {
  const queryClient = useQueryClient()
  return (options: { otherTabs: boolean }) => {
    void queryClient.invalidateQueries({ queryKey: ['tabs', id] })
    void queryClient.invalidateQueries({ queryKey: ['validation-summary', id] })
    void queryClient.invalidateQueries({ queryKey: ['engagement', id] })
    setTimeout(() => void queryClient.invalidateQueries({ queryKey: ['engagement', id] }), 2000)
    if (options.otherTabs) void queryClient.invalidateQueries({ queryKey: ['tab', id] })
  }
}

export function useSaveTab(id: string, tabNo: number) {
  const queryClient = useQueryClient()
  const refresh = useRefreshAfterWrite(id)
  return useMutation({
    mutationFn: (sections: SectionPatch[]) => api<SaveTabResult>(`/engagements/${id}/tabs/${tabNo}`, { method: 'PATCH', body: { sections } }),
    onSuccess: (result) => {
      queryClient.setQueryData(['tab', id, tabNo], result.tab)
      queryClient.setQueryData(['validation-summary', id], result.summary)
      refresh({ otherTabs: result.visibilityChanges.length > 0 })
    },
  })
}

export function useUpdateEngagement(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ request, version }: { request: UpdateEngagementRequest; version: string }) =>
      api<EngagementDetail>(`/engagements/${id}`, { method: 'PATCH', body: request, ifMatch: version }),
    onSuccess: (updated) => {
      queryClient.setQueryData(['engagement', id], updated)
      void queryClient.invalidateQueries({ queryKey: ['participants', id] })
      void queryClient.invalidateQueries({ queryKey: ['engagements'] })
    },
    // A conflict means our copy is stale: refetch so the next attempt carries the current version.
    onError: () => void queryClient.invalidateQueries({ queryKey: ['engagement', id] }),
  })
}

export function useUploadAttachment(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ path, file }: { path: string; file: File }) => uploadFile<Attachment>(`/engagements/${id}${path}`, file),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['attachments', id] })
      // The backend re-evaluates "attachment required" asynchronously after AttachmentAdded.
      const refresh = () => {
        void queryClient.invalidateQueries({ queryKey: ['tab', id] })
        void queryClient.invalidateQueries({ queryKey: ['tabs', id] })
        void queryClient.invalidateQueries({ queryKey: ['validation-summary', id] })
      }
      refresh()
      setTimeout(refresh, 1500)
    },
  })
}

/**
 * Submit for review. The backend refuses with 422 plus the blocking issues; the client has no readiness logic.
 * No If-Match: ordinary saves also move the engagement's version, so the status guard on the server decides.
 */
export function useSubmit(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<EngagementDetail>(`/engagements/${id}/submit`, { method: 'POST' }),
    onSuccess: (updated) => {
      queryClient.setQueryData(['engagement', id], updated)
      for (const key of [['tabs', id], ['tab', id], ['validation-summary', id], ['attachments', id], ['review', id], ['engagements']])
        void queryClient.invalidateQueries({ queryKey: key })
    },
    onError: () => void queryClient.invalidateQueries({ queryKey: ['engagement', id] }),
  })
}
