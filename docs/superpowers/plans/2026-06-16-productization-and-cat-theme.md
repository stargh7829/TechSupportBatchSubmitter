# Productization And Cat Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship v1.4.0 with safer Excel intake, exportable diagnostics, versioned portable packages, and a restrained cat-themed UI.

**Architecture:** Keep domain validation in `TechSupportBatchSubmitter.Core`, diagnostics and UI orchestration in `TechSupportBatchSubmitter.Wpf`, and release packaging in `scripts`. Avoid broad rewrites of existing queue/platform code.

**Tech Stack:** .NET 8, WPF, ClosedXML, WebView2, PowerShell 7.

---

## Tasks

- [ ] Add workbook diagnostic data to `WorkbookLoadResult` and `ExcelWorkbookRepository`.
- [ ] Add validator tests for duplicate headers, blank templates, overlong fields, and invalid ticket numbers.
- [ ] Add diagnostic package exporter in WPF services.
- [ ] Add an export button and status text to the UI.
- [ ] Add cat ear and cat paw UI decorations without changing workflow density.
- [ ] Bump version to `1.4.0`, update docs, and make build script emit versioned and latest ZIP files.
- [ ] Run Release tests and portable package build.
