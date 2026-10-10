import { Route, Routes } from 'react-router-dom'
import { DashboardPage } from '@features/dashboard/DashboardPage'
import { EngagementPage } from '@features/engagement/EngagementPage'
import { ReviewPage } from '@features/review/ReviewPage'
import { LoginPage } from '@shared/auth/LoginPage'
import { RequireAuth } from '@shared/auth/RequireAuth'
import { ServiceHealthPanel } from '@shared/health/ServiceHealthPanel'

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      {/* Service health from Phase 0, handy during the demo. */}
      <Route path="/status" element={<main className="mx-auto max-w-3xl p-8"><ServiceHealthPanel /></main>} />
      <Route element={<RequireAuth />}>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/engagements/:id" element={<EngagementPage />} />
        <Route path="/engagements/:id/review" element={<ReviewPage />} />
      </Route>
    </Routes>
  )
}
