// Mirrors contracts/api DTOs. The backend decides every status, role, capability and action; the client only renders them.

export type EngagementStatus = 'Draft' | 'InProgress' | 'UnderReview' | 'Approved' | 'Rejected'
export type EngagementRole = 'Auditor' | 'CoAuditor' | 'Reviewer' | 'Viewer'

export interface CurrentUser {
  userId: string
  displayName: string
  email: string
  homeRole: EngagementRole
}

export interface LoginResponse {
  accessToken: string
  expiresAt: string
  user: CurrentUser
}

export interface Permissions {
  canCreateEngagement: boolean
}

export interface UserSummary {
  userId: string
  displayName: string
  homeRole: EngagementRole
}

export interface Person {
  userId: string
  displayName: string
}

export interface EngagementSummary {
  engagementId: string
  name: string
  region: string
  periodType: string
  reviewer: Person
  status: EngagementStatus
  lastUpdatedAt: string
  myRole: EngagementRole
  allowedActions: string[]
}

export interface EngagementList {
  items: EngagementSummary[]
  counts: Record<EngagementStatus, number>
  total: number
  page: number
  pageSize: number
}

export interface EngagementDetail {
  engagementId: string
  name: string
  region: string
  periodType: string
  status: EngagementStatus
  owner: Person
  reviewer: Person
  myRole: EngagementRole
  allowedActions: string[]
  capabilities: string[]
  version: string
  createdAt: string
  lastUpdatedAt: string
}

export interface Participant {
  userId: string
  displayName: string
  role: EngagementRole
}

export interface CreationOptions {
  periodTypes: string[]
  regions: string[]
  configTypes: string[]
  options: { key: string; label: string; type: string }[]
}

export interface CreateEngagementRequest {
  name: string
  periodType: string
  region: string
  reviewerUserId: string
  participantUserIds: string[]
  configuration: { configTypes: string[]; options: Record<string, boolean> }
}

export type ListScope = 'participating' | 'owned' | 'assigned' | 'reviewing'

// ---- Workflow (Phase 2). The backend decides visibility, editability, required flags and issues; the client renders them. ----

export type IssueSeverity = 'Error' | 'Warning' | 'Information'
export type IssueScope = 'Field' | 'Section' | 'Tab' | 'Engagement'
export type VarianceIndicator = 'Ok' | 'Warning' | 'Significant' | 'Missing'
export type FieldType = 'checkbox' | 'text' | 'number' | 'date' | 'documentation'
export type FieldValue = boolean | string | number | null

export interface Issue {
  code: string
  severity: IssueSeverity
  scope: IssueScope
  message: string
  tabNo: number | null
  sectionKey: string | null
  fieldKey: string | null
  isBlocking: boolean
}

export interface IssueCounts {
  errors: number
  warnings: number
  infos: number
}

export interface TabSummary {
  tabNo: number
  title: string
  issueCounts: IssueCounts
}

/** `ready` is false while workflow-service is still provisioning a new engagement. */
export interface TabsResponse {
  ready: boolean
  tabs: TabSummary[]
}

export interface FieldView {
  key: string
  type: FieldType
  label: string
  value: FieldValue
  required: boolean
  editable: boolean
  issues: Issue[]
}

/** Absent (null) for roles that cannot see attachments, e.g. Viewer. */
export interface SectionAttachmentInfo {
  allowed: boolean
  required: boolean
  canUpload: boolean
  hasCurrent: boolean
}

export interface SectionView {
  sectionKey: string
  title: string
  editable: boolean
  version: string
  fields: FieldView[]
  attachment: SectionAttachmentInfo | null
  issues: Issue[]
}

export interface ReconciliationRow {
  comparisonKey: string
  label: string
  expected: number | null
  actual: number | null
  valueVariance: number | null
  percentVariance: number | null
  indicator: VarianceIndicator
  isSignificant: boolean
  isMissing: boolean
}

export interface TabView {
  tabNo: number
  title: string
  editable: boolean
  sections: SectionView[]
  issues: Issue[]
  reconciliation: ReconciliationRow[] | null
}

export interface ValidationSummary {
  counts: IssueCounts
  readyToSubmit: boolean
  issues: Issue[]
  reconciliation: ReconciliationRow[]
}

export interface VisibilityChange {
  sectionKey: string
  visible: boolean
}

export interface SaveTabResult {
  tab: TabView
  summary: ValidationSummary
  visibilityChanges: VisibilityChange[]
}

export interface SectionPatch {
  sectionKey: string
  version: string
  fields: Record<string, FieldValue>
}

export type AttachmentScope = 'Engagement' | 'Tab' | 'Section'

export interface Attachment {
  attachmentId: string
  engagementId: string
  scope: AttachmentScope
  tabNo: number | null
  sectionKey: string | null
  fileName: string
  contentType: string
  sizeBytes: number
  uploadedByName: string
  uploadedAt: string
}

export interface UpdateEngagementRequest {
  name?: string
  periodType?: string
  region?: string
  reviewerUserId?: string
}

// ---- Review and real time (Phase 3) ----

export interface TabComment {
  commentId: string
  tabNo: number
  cycle: number
  authorUserId: string
  authorName: string
  body: string
  createdAt: string
}

export interface ReviewDecision {
  decisionId: string
  cycle: number
  decision: 'Approved' | 'Rejected'
  reviewerUserId: string
  reviewerName: string
  comment: string | null
  decidedAt: string
}

/** `available` is false when workflow-service could not serve this tab; the screen degrades instead of failing. */
export interface ReviewTab {
  tabNo: number
  title: string
  available: boolean
  content: TabView | null
}

/** Everything the review screen renders. canComment / canDecide are decided by the backend. */
export interface ReviewView {
  engagement: EngagementDetail
  cycle: number
  canComment: boolean
  canDecide: boolean
  tabs: ReviewTab[]
  validation: ValidationSummary | null
  comments: TabComment[]
  history: ReviewDecision[]
}

export interface DecisionResult {
  decisionId: string
  cycle: number
  decision: 'Approved' | 'Rejected'
}

/** SSE frame data. Carries ids, names and versions only, never field values. */
export interface StreamEnvelope {
  eventId: string
  eventType: string
  schemaVersion: number
  engagementId: string
  occurredAt: string
  correlationId: string
  actor: { userId: string; name: string | null } | null
  payload: Record<string, unknown>
}
