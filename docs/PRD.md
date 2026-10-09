# AuditFlow Product Requirements Document

> Converted from `docs/auditflow_prd.docx` (plain-text extraction; tables flattened one cell per line). The .docx is the original.

Overview

AuditFlow is a backend-controlled audit engagement management platform for financial audit workflows. It is designed to manage the full lifecycle of an audit engagement from setup and data collection through review, reconciliation, and approval, while preserving strong security, role-based access, observability, and consistent multi-user collaboration.[cite:1]

The product is intended to support document-heavy audit operations in which multiple users work on the same engagement at the same time. The platform combines a React-based presentation layer with backend-authoritative workflow rules, permissions, validations, and targeted real-time event updates to ensure consistency across all authorized users.[cite:1]

Product Vision

The goal of AuditFlow is to replace fragmented or manual audit processes with a centralized system that improves efficiency, productivity, visibility, and operational control. The system should allow teams to create, progress, review, and finalize audit engagements in a way that is secure, traceable, scalable, and enterprise-ready.[cite:1]

Business Problem

Financial audits play a critical role in maintaining control, transparency, and compliance in investment banking operations. As these workflows become increasingly digital and collaborative, traditional systems struggle to provide consistent engagement state, reliable review workflows, proper observability, and secure handling of sensitive audit information.[cite:1]

AuditFlow addresses this problem by offering:

A structured engagement lifecycle

Rule-based workflow configuration

Fine-grained role-based access control

Centralized document and data handling

Targeted real-time updates for multi-user consistency

Operational monitoring across distributed services[cite:1]

Goals

Primary Goals

Provide a single system to manage the end-to-end audit engagement lifecycle.[cite:1]

Ensure backend-controlled workflow consistency for concurrent users.[cite:1]

Support secure role-based collaboration between auditors, co-auditors, reviewers, and viewers.[cite:1]

Enable reviewer approval and rejection with comments through a separate review workflow.[cite:1]

Deliver strong observability, scalability, and maintainability through a microservices architecture using Azure services.[cite:1]

Non-Goals for MVP

Document version history

Advanced reporting and analytics

Admin-facing rule engine management UI

Field-level audit trail playback

Complex external system integrations

Users and Roles

Role

Description

Core Permissions

Auditor

Primary engagement contributor

Create engagements, edit all tabs, upload attachments at any level, submit for review

Co-Auditor

Collaborating contributor

Same permissions as Auditor, including editing all tabs and submitting for review

Reviewer

Final approver

View all submitted data, add comments at tab level, approve or reject from review screen

Viewer

Read-only participant

View engagement details only; no attachments and no comments

Core Concepts

Engagement

An Engagement is the primary business object in the system. It represents a complete audit file and acts as the top-level container for workflow steps, sections, attachments, validations, comments, status transitions, and review actions.

Backend-Controlled UI

AuditFlow follows a backend-controlled UI model. The backend is the single source of truth for workflow progression, role permissions, validation outcomes, section visibility, and engagement status. The frontend renders backend-provided state and reacts to backend-driven updates instead of owning critical business decisions locally.[cite:1]

Real-Time Updates

The system uses Server-Sent Events to push only changed sections or domain events rather than the full engagement payload. This reduces unnecessary load and supports consistent state propagation across concurrent users working on the same engagement.[cite:1]

User Journey

A user logs in through OAuth 2.0 and receives role-based access via JWT claims.[cite:1]

The user opens the dashboard and sees engagement list view, status cards, and summary counts relevant to their role.

An Auditor or Co-Auditor creates a new Engagement.

The engagement is configured in the first tab, which determines rules and downstream workflow behavior.

Users complete data entry, upload attachments, and add documentation across the remaining tabs.

Real-time backend updates propagate changed sections to other authorized users.

The engagement is submitted for review.

The Reviewer opens a separate review screen, adds tab-level comments, and approves or rejects the engagement.

If rejected, the engagement returns to In Progress for rework.

Once approved, the engagement becomes final and read-only for standard workflow actions.

Engagement Lifecycle

Statuses

Draft

In Progress

Under Review

Approved

Rejected

Status Rules

New engagements begin in Draft.

Once meaningful work starts, the engagement moves to In Progress.

Once submitted for review, the engagement moves to Under Review.

Reviewer approval moves the engagement to Approved.

Reviewer rejection moves the engagement to Rejected.

After rejection and subsequent auditor rework, the engagement returns to In Progress.

Workflow Structure

The MVP uses a five-tab workflow.

Tab

Name

Business Purpose

1

Input Configuration

Defines period type, region, configuration options, and rule-driven behavior for later steps

2

Data Collection

Captures required audit data and supporting attachments

3

Audit Procedures

Records operational checks, validations, decisions, and justifications

4

Documentation & Notes

Consolidates explanations, contextual notes, and reviewer-facing support details

5

Reconciliation & Submission

Displays percentage-based and value-based comparisons, validation outcomes, and submission readiness

Tab Behavior

1. Input Configuration

This tab controls the downstream workflow. It captures initial setup values and rule-based options that determine which sections, validations, and content should appear in later tabs.

2. Data Collection

This tab captures form data and supporting attachments relevant to the engagement. It supports structured user input and the latest uploaded supporting documents.

3. Audit Procedures

This tab represents the execution layer of the audit process. It contains step-oriented sections where users record checks, observations, selections, dates, and supporting rationale.

4. Documentation & Notes

This tab is used to explain the engagement in a reviewer-friendly way. It centralizes contextual notes, supporting explanations, and documentation needed to understand earlier decisions.

5. Reconciliation & Submission

This tab presents final comparisons and submission readiness. It should include percentage-based and value-based reconciliation outputs, highlight significant variances, and show whether the engagement is valid for submission.

MVP Functional Requirements

FR-1 Authentication and Authorization

The system must support OAuth 2.0 authentication with JWT-based authorization.[cite:1]

User roles must be enforced from backend claims and authorization rules.[cite:1]

Unauthorized users must not access restricted engagements or actions.

FR-2 Dashboard

The system must provide a dashboard with:

Engagement list view

Status cards

Summary counts

Filters for owned, assigned, reviewing, or participating engagements

Dashboard content must vary by user role and access scope.

FR-3 Engagement Creation

Authorized users must be able to create a new Engagement.

Mandatory fields at creation include:

Engagement name or identifier

Period type

Region

Reviewer

Relevant configuration types

Rule-based setup options

Creation must initialize the workflow structure and required metadata.

FR-4 Rule-Based Configuration

The first tab must determine the visibility and validation behavior of downstream tabs and sections.

Rule-based behavior must be stored in backend-managed logic and configuration, not hardcoded in the client.

Changes to configuration should immediately affect relevant workflow sections.

FR-5 Section-Based Data Entry

Each tab may contain multiple sections.

A section may include:

Checkboxes

Text inputs

Date inputs

Documentation text

Attachment uploads

Validation rules must apply at field, section, and tab levels.

FR-6 Attachments

The system must support attachments at:

Section level

Tab level

Whole-engagement level

The MVP only requires visibility of the latest uploaded file and does not include document version history.

Viewers must not be able to see attachments.

FR-7 Permissions by Role

Auditors and Co-Auditors must be able to edit all tabs.

Reviewers must not edit engagement content.

Reviewers must be able to add comments at tab level.

Viewers must only view engagement details.

Viewers must not see attachments or comments.

FR-8 Validation and Messaging

The system must show validation outcomes through top-level banners or equivalent visible messaging.

Messages may be of type:

Error

Warning

Information

These messages must guide users on what is incomplete or required before progression or submission.

FR-9 Submission Workflow

Auditors and Co-Auditors must be able to submit an engagement for review.

Submission should only succeed if required validations pass.

Submission must transition the engagement to Under Review.

FR-10 Review Workflow

Approval and rejection must happen from a separate review screen after submission.

Reviewers must be able to:

View the full engagement

Add tab-level comments

Approve the engagement

Reject the engagement with comments

Rejection should route the engagement back for rework.

FR-11 Real-Time Change Propagation

The system must push only changed sections or domain events via SSE instead of full engagement refreshes.

Authorized users viewing the same engagement should see near-real-time updates for relevant changes.

The client must reconcile incoming updates against current UI state without bypassing backend authority.

FR-12 Audit Summary and Reconciliation

The final workflow tab must display reconciliation outputs based on percentage-based and value-based comparisons.

The MVP may support generalized variance indicators without requiring final production-grade financial formulas.

Significant mismatches or missing values should be clearly surfaced before submission.

Review Screen Requirements

The review screen is a distinct application view separate from the editable workflow tabs.

Review Screen Features

Read-only rendering of engagement content for reviewer

Tab-level comments

Approval action

Rejection action with mandatory comment

Review decision history summary

Validation summary for the full engagement

Dashboard Requirements

Dashboard Components

Summary cards for Draft, In Progress, Under Review, Approved, and Rejected engagements

Search and filters

Engagement list with key metadata

Role-sensitive quick actions

Suggested Engagement List Columns

Engagement ID

Engagement Name

Region

Period Type

Reviewer

Current Status

Last Updated

User Role in Engagement

Domain Model

Primary Entities

User

Role

Engagement

EngagementParticipant

EngagementConfiguration

WorkflowTab

WorkflowSection

SectionField

Attachment

TabComment

ReviewDecision

ValidationIssue

ReconciliationSummary

DomainEvent

Entity Notes

Engagement owns the workflow and overall lifecycle.

EngagementParticipant maps users to an engagement and role.

EngagementConfiguration stores rule-based settings selected in tab one.

WorkflowTab models the five-step structure.

WorkflowSection represents each section within a tab.

SectionField stores user-entered values.

Attachment supports section, tab, or engagement scope.

TabComment stores reviewer comments.

ReviewDecision stores approve or reject outcomes.

ValidationIssue stores warning, info, or error outcomes.

ReconciliationSummary stores final comparison outputs.

DomainEvent supports event-driven propagation and downstream integration.

Service Architecture

The platform should use a microservices architecture, which aligns with the audit modernization and distributed systems experience referenced in the source resume.[cite:1]

Suggested Services

Service

Responsibility

Auth Service

OAuth integration, JWT issuance, role claims, access checks

Engagement Service

Engagement creation, retrieval, participants, status lifecycle

Workflow Service

Tabs, sections, field values, rule-driven section visibility, validation logic

Attachment Metadata Service

Attachment references, metadata, scoped access control

Review Service

Review screen, comments, approval, rejection, review history

Notification/Event Service

Domain events, SSE streaming, event routing

Observability/Platform Layer

Telemetry, tracing, diagnostics, operational monitoring

Communication Patterns

Synchronous APIs for user-driven reads and writes

Asynchronous event publication for workflow changes and downstream processing

SSE for client-facing real-time updates

Azure Service Bus topics and queues for service communication[cite:1]

API Planning

Suggested API Domains

Auth APIs

POST /auth/login

GET /auth/me

GET /auth/permissions

Engagement APIs

POST /engagements

GET /engagements

GET /engagements/{id}

PATCH /engagements/{id}

POST /engagements/{id}/submit

GET /engagements/{id}/participants

Workflow APIs

GET /engagements/{id}/tabs

GET /engagements/{id}/tabs/{tabId}

PATCH /engagements/{id}/tabs/{tabId}

PATCH /engagements/{id}/sections/{sectionId}

GET /engagements/{id}/validation-summary

Attachment APIs

POST /engagements/{id}/attachments

POST /tabs/{tabId}/attachments

POST /sections/{sectionId}/attachments

GET /engagements/{id}/attachments

Review APIs

GET /reviews/{engagementId}

POST /reviews/{engagementId}/comments

POST /reviews/{engagementId}/approve

POST /reviews/{engagementId}/reject

SSE/Event APIs

GET /engagements/{id}/events/stream

Event Model

Event Principles

Events should describe meaningful domain changes.

Events should contain only what clients or downstream consumers need.

Events should be scoped to changed sections or actions where possible.

Example Event Types

EngagementCreated

EngagementUpdated

ConfigurationChanged

SectionUpdated

AttachmentAdded

ValidationSummaryChanged

EngagementSubmitted

ReviewerCommentAdded

ReviewApproved

ReviewRejected

EngagementStatusChanged

Example SSE Payload

{  "eventType": "SectionUpdated",  "engagementId": "ENG-1024",  "tabId": "tab-3",  "sectionId": "section-3-2",  "timestamp": "2026-05-15T00:00:00Z",  "changedFields": ["controlOwner", "reviewDate"],  "version": 14}

Data and Consistency Requirements

The backend must remain the sole authority for persisted engagement state.

Clients should not finalize business state transitions locally.

Concurrency handling must protect against conflicting updates when multiple users edit the same engagement.[cite:1]

State must remain consistent for approximately 100 concurrent users across active engagements.

Real-time event traffic should remain incremental and scoped to reduce load.[cite:1]

Suggested Consistency Strategies

Optimistic concurrency with row versioning

Domain validation before persistence

Event publication after successful transaction completion

Version identifiers in update responses and SSE payloads

Non-Functional Requirements

Security

Enforce OAuth 2.0 with JWT-based role claims.[cite:1]

Secure secrets via Azure Key Vault.[cite:1]

Restrict engagement access to authorized participants only.

Protect attachment visibility by role and scope.

Ensure review actions are permission-gated and auditable.

Observability

Use Azure Application Insights for telemetry, diagnostics, and monitoring.[cite:1]

Track request traces, dependency calls, failures, and latency.[cite:1]

Correlate user-facing workflow issues with service-level logs and events.

Performance

Support roughly 100 concurrent users across active engagements.

Avoid full engagement refreshes for every change.

Minimize payload size for real-time updates.

Maintain responsive dashboard and engagement screen performance.

Scalability

Support microservice isolation and independent service evolution.[cite:1]

Use Azure Service Bus and Azure Functions for asynchronous scale-out scenarios.[cite:1]

Keep service contracts stable and explicit.

Maintainability

Follow SOLID principles and appropriate design patterns across the codebase, consistent with the engineering approach described in the resume.[cite:1]

Separate concerns by domain and service boundary.

Keep business rules in backend services rather than duplicating logic in the client.

UX Requirements

General UX

The interface should be simple and minimal.

The dashboard should be easily scannable.

The workflow should make the five tabs clearly visible.

Validation banners should be prominent and understandable.

Role-based action visibility should reduce confusion.

Engagement Screen UX

Clear tab navigation

Visible current status

Prominent save/update feedback

Attachment support at the correct scope

Clear submission readiness indicators

Read-only mode for unauthorized edits

Reviewer UX

Separate review screen

Easy tab-level commenting

Clear approve/reject actions

Strong visual distinction between review and edit modes

Suggested Repository Planning

A practical repository structure for implementation planning is:

AuditFlow/├── frontend/│   ├── apps/web│   ├── features/dashboard│   ├── features/engagement│   ├── features/review│   └── shared/├── services/│   ├── auth-service/│   ├── engagement-service/│   ├── workflow-service/│   ├── review-service/│   ├── attachment-service/│   └── notification-service/├── contracts/│   ├── api/│   └── events/├── infra/│   ├── local/│   ├── docker/│   └── azure/├── docs/│   ├── architecture/│   ├── api/│   └── decisions/└── tests/

Implementation Phases

Phase 1: Foundation

Auth flow

Role model

Engagement creation

Dashboard shell

Base microservice skeleton

Shared contracts

Phase 2: Workflow Core

Five-tab engagement flow

Rule-based configuration

Section data entry

Validation framework

Attachment handling

Phase 3: Review and Real-Time Updates

Review screen

Tab-level comments

Submit/approve/reject flow

SSE infrastructure

Incremental event propagation

Phase 4: Hardening

Observability integration

Concurrency safeguards

Permission audit

Performance tuning

Deployment pipeline

GitHub Copilot Planning Prompt

Use the following prompt with GitHub Copilot to break this PRD into implementation tasks:

You are helping implement AuditFlow, a backend-controlled audit engagement management platform built with React, Tailwind CSS, ASP.NET Core Web API, Dapper, SQL Server, Azure Service Bus, Azure Functions, Azure Key Vault, and Application Insights.Please read the PRD and generate:1. An implementation roadmap by milestone2. Backend microservice breakdown3. Database schema proposal4. REST API contract proposal5. SSE event contract proposal6. Frontend page and component breakdown7. User stories with acceptance criteria8. Technical risks and mitigations9. Suggested repository structure10. A phased backlog for MVP deliveryConstraints:- Backend-controlled UI- OAuth 2.0 + JWT roles- Roles: Auditor, Co-Auditor, Reviewer, Viewer- Five-tab workflow- Reviewer comments at tab level only- Review actions happen on separate review screen- Attachments supported at section, tab, and engagement levels- Rejected engagements return to In Progress- SSE sends only changed sections or events- Rule-based first tab controls downstream workflow- Microservices only, no modular monolith fallback- Viewer cannot see comments or attachments

Open Decisions

The following items can be refined during implementation planning:

Exact reconciliation formulas and thresholds

Attachment storage strategy and physical file store integration

Detailed rule engine representation for tab one configuration

Final comment threading model

Exact audit trail depth for MVP

Whether Approved engagements are fully locked or partially editable under exception rules

Acceptance Summary

The MVP will be considered successful if it can:

Authenticate users with role-aware access.[cite:1]

Create and manage Engagements through a five-tab workflow.

Apply rule-driven downstream rendering from tab one.

Support attachments at all required scopes.

Allow Auditors and Co-Auditors to edit all tabs.

Allow Reviewers to comment, approve, and reject from a separate review screen.

Return rejected engagements to In Progress.

Show validation messages clearly.

Push incremental engagement updates through SSE.

Maintain backend-controlled consistency across concurrent users.[cite:1]

