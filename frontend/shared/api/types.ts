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
