import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import ProfileKycSummary from '../src/product/ProfileKycSummary';
const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 's', subjectVersion: 1, expiresAt: '2027-01-01T00:00:00Z' };
const policy = { revision: 'a'.repeat(64), notice: 'An operator published review notice.', reviewers: [{ actorId: 'A200', name: 'Reviewer' }] };
const item = { tenantId: 'T001', customerId: 'C001', customerName: 'Customer', ownerActorId: 'A100', version: 1, state: 'MANUAL_REVIEW_ACCEPTED', reviewerId: 'A200', reviewerName: 'Reviewer', policyRevision: policy.revision, notice: policy.notice, evidence: [{ documentVersionId: 'V001', purpose: 'IDENTITY', fileName: 'identity.pdf', sha256: 'b'.repeat(64) }], reason: 'Reviewed supplied evidence', recordedBy: 'A200', recordedAt: '2026-10-21T00:00:00Z', operationId: 'c'.repeat(32) };
const response = (current: unknown = item, p: unknown = policy) => ({ tenantId: 'T001', customerId: 'C001', current, policy: p });
function mount(api: AccessApi) { return render(<FoundationProvider appearance="midnight"><ProfileKycSummary api={api} tenant="T001" actor="A100" customer="C001" /></FoundationProvider>); }
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
it.each([['SUBMITTED','Awaiting review'],['WITHDRAWN','Withdrawn'],['MANUAL_REVIEW_ACCEPTED','Manual review accepted'],['CHANGES_REQUESTED','Changes requested'],['REJECTED','Review declined']])('shows owner state %s with the review action and precise verification boundary', async (state,label) => {
 const api=new AccessApi(session);const call=vi.spyOn(api,'basicKyc').mockResolvedValue(response({...item,state}));mount(api);
 expect(await screen.findByText(label)).toBeVisible();expect(screen.getByText('Reviewer')).toBeVisible();expect(screen.getByRole('link',{name:'Open Basic KYC and review history'})).toHaveAttribute('href','/kyc');expect(screen.getByText(/separate from government/)).toBeVisible();expect(call).toHaveBeenCalledWith('/me','GET',undefined,expect.any(AbortSignal));
});
it('distinguishes no submission from configuration unavailable and retains historical decisions when disabled',async()=>{
 const api=new AccessApi(session);const call=vi.spyOn(api,'basicKyc').mockResolvedValue(response(null,null));mount(api);
 expect(await screen.findByText('Not submitted')).toBeVisible();expect(screen.getByText(/has not enabled/)).toBeVisible();call.mockResolvedValue(response(item,null));fireEvent.click(screen.getByRole('button',{name:'Refresh KYC status'}));expect(await screen.findByText('Manual review accepted')).toBeVisible();
});
it.each([{...item,tenantId:'T002'},{...item,customerId:'C002'},{...item,ownerActorId:'A900'},{...item,state:'VERIFIED'}])('hides foreign or fabricated state',async current=>{
 const api=new AccessApi(session);vi.spyOn(api,'basicKyc').mockResolvedValue(response(current));mount(api);expect(await screen.findByText(/status could not be verified/)).toBeVisible();expect(screen.queryByText('Reviewer')).not.toBeInTheDocument();
});
it('clears old review information immediately on refresh and after failure',async()=>{
 const api=new AccessApi(session);const call=vi.spyOn(api,'basicKyc').mockResolvedValue(response());mount(api);await screen.findByText('Manual review accepted');let reject:(e:Error)=>void=()=>undefined;call.mockImplementation(()=>new Promise((_,r)=>{reject=r;}));fireEvent.click(screen.getByRole('button',{name:'Refresh KYC status'}));expect(screen.queryByText('Manual review accepted')).not.toBeInTheDocument();await act(async()=>{reject(new Error('denied'));await Promise.resolve();});expect(await screen.findByText(/status could not be verified/)).toBeVisible();
});
it('ignores late response after unmount',async()=>{
 const api=new AccessApi(session);let resolve:(v:unknown)=>void=()=>undefined;const call=vi.spyOn(api,'basicKyc').mockImplementation(()=>new Promise(r=>{resolve=r;}));const v=mount(api);await waitFor(()=>{expect(call).toHaveBeenCalledOnce();});v.unmount();await act(async()=>{resolve(response());await Promise.resolve();});expect(screen.queryByText('Reviewer')).not.toBeInTheDocument();
});
it('explains changed notice without silently changing the stored case status',async()=>{
 const api=new AccessApi(session);vi.spyOn(api,'basicKyc').mockResolvedValue(response({...item,state:'SUBMITTED',policyRevision:'d'.repeat(64)}));mount(api);expect(await screen.findByText('Awaiting review')).toBeVisible();expect(screen.getByText(/withdraw and resubmit/)).toBeVisible();
});
