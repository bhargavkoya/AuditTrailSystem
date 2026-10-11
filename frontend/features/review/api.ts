import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@shared/api/client'
import type { DecisionResult, ReviewView, TabComment } from '@shared/api/types'

export const useReview = (id: string) =>
  useQuery({ queryKey: ['review', id], queryFn: () => api<ReviewView>(`/reviews/${id}`), retry: false })

/** The status change follows from an event, so refetch now and again shortly after (the stream usually beats the timers). */
function useRefreshReview(id: string) {
  const queryClient = useQueryClient()
  return () => {
    const refresh = () => {
      void queryClient.invalidateQueries({ queryKey: ['review', id] })
      void queryClient.invalidateQueries({ queryKey: ['engagement', id] })
      void queryClient.invalidateQueries({ queryKey: ['engagements'] })
    }
    refresh()
    setTimeout(refresh, 1500)
    setTimeout(refresh, 4000)
  }
}

export function useAddComment(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: { tabNo: number; body: string }) => api<TabComment>(`/reviews/${id}/comments`, { method: 'POST', body: request }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['review', id] }),
  })
}

export function useDecide(id: string, decision: 'approve' | 'reject') {
  const refresh = useRefreshReview(id)
  return useMutation({
    mutationFn: (comment: string) => api<DecisionResult>(`/reviews/${id}/${decision}`, { method: 'POST', body: { comment } }),
    onSuccess: refresh,
    onError: refresh,
  })
}
