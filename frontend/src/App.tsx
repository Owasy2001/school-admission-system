import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  admissionApi,
  ApiError,
  HoldStatusResponse,
  normalizeDigits,
  SchoolClassItem,
  StudentHoldItem
} from './api/client';

const STUDENT_ID_STORAGE_KEY = 'school_admission_student_id';
const SELECTED_HOLD_STORAGE_KEY = 'school_admission_selected_hold_id';

type ActiveTab = 'all' | 'classes' | 'application' | 'my-applications' | 'admin';

export function isValidStudentId(id: string): boolean {
  const normalized = normalizeDigits(id.trim());
  return normalized.length === 17;
}

export function formatRemainingTime(expiresAtIso: string, nowMs: number): {
  expired: boolean;
  remainingSeconds: number;
  formatted: string;
} {
  const targetMs = new Date(expiresAtIso).getTime();
  if (Number.isNaN(targetMs)) {
    return { expired: true, remainingSeconds: 0, formatted: '00:00' };
  }

  const diffSec = Math.max(0, Math.ceil((targetMs - nowMs) / 1000));
  const mins = Math.floor(diffSec / 60);
  const secs = diffSec % 60;
  const formatted = `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;

  return {
    expired: diffSec <= 0,
    remainingSeconds: diffSec,
    formatted
  };
}

function generateSampleStudentId(): string {
  const randomSuffix = String(Math.floor(1000000000 + Math.random() * 9000000000));
  return `2015269${randomSuffix}`.slice(0, 17);
}

export function App() {
  const [activeTab, setActiveTab] = useState<ActiveTab>('all');
  const studentInputRef = useRef<HTMLInputElement | null>(null);
  const applicationPanelRef = useRef<HTMLElement | null>(null);

  const [studentId, setStudentId] = useState<string>(() => {
    try {
      const saved = localStorage.getItem(STUDENT_ID_STORAGE_KEY) || '';
      return normalizeDigits(saved).slice(0, 17);
    } catch {
      return '';
    }
  });

  const [selectedHoldId, setSelectedHoldId] = useState<string | null>(() => {
    try {
      return localStorage.getItem(SELECTED_HOLD_STORAGE_KEY);
    } catch {
      return null;
    }
  });

  const [classes, setClasses] = useState<SchoolClassItem[]>([]);
  const [myHolds, setMyHolds] = useState<StudentHoldItem[]>([]);
  const [selectedHoldDetail, setSelectedHoldDetail] = useState<HoldStatusResponse | null>(null);

  const [classNameInput, setClassNameInput] = useState<string>('');
  const [totalSeatsInput, setTotalSeatsInput] = useState<string>('120');

  const [loadingClasses, setLoadingClasses] = useState<boolean>(true);
  const [submittingClass, setSubmittingClass] = useState<boolean>(false);
  const [busyActionId, setBusyActionId] = useState<string | null>(null);

  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const [nowMs, setNowMs] = useState<number>(() => Date.now());
  const expiredSyncTriggeredRef = useRef<Set<string>>(new Set());

  const studentIdValid = useMemo(() => isValidStudentId(studentId), [studentId]);

  const updateStudentIdValue = useCallback((rawValue: string) => {
    const cleaned = normalizeDigits(rawValue).slice(0, 17);
    setStudentId((prev) => {
      if (prev !== cleaned) {
        setSelectedHoldId(null);
        setSelectedHoldDetail(null);
        setMyHolds([]);
      }
      return cleaned;
    });
    setErrorMessage(null);
  }, []);

  useEffect(() => {
    try {
      localStorage.setItem(STUDENT_ID_STORAGE_KEY, studentId);
    } catch {
      // ignore storage errors
    }
  }, [studentId]);

  useEffect(() => {
    try {
      if (selectedHoldId) {
        localStorage.setItem(SELECTED_HOLD_STORAGE_KEY, selectedHoldId);
      } else {
        localStorage.removeItem(SELECTED_HOLD_STORAGE_KEY);
      }
    } catch {
      // ignore storage errors
    }
  }, [selectedHoldId]);

  const fetchClasses = useCallback(async (silent = false) => {
    try {
      const data = await admissionApi.getClasses();
      setClasses(data);
      if (!silent) {
        setErrorMessage((prev) =>
          prev?.includes('Unable to connect') ? null : prev
        );
      }
    } catch (err) {
      if (!silent) {
        const msg =
          err instanceof ApiError ? err.message : 'Failed to load class list.';
        setErrorMessage(msg);
      }
    } finally {
      setLoadingClasses(false);
    }
  }, []);

  const fetchStudentData = useCallback(
    async (currentStudentId: string, currentHoldId: string | null, silent = false) => {
      if (!isValidStudentId(currentStudentId)) {
        setMyHolds([]);
        setSelectedHoldDetail(null);
        return;
      }

      try {
        const holds = await admissionApi.getMyHolds(currentStudentId);
        setMyHolds(holds);

        const targetHoldId =
          currentHoldId && holds.some((h) => h.holdId === currentHoldId)
            ? currentHoldId
            : holds.find((h) => h.status === 'HELD')?.holdId || holds[0]?.holdId || null;

        if (targetHoldId !== currentHoldId) {
          setSelectedHoldId(targetHoldId);
        }

        if (targetHoldId) {
          const holdDetail = await admissionApi.getHold(targetHoldId, currentStudentId);
          setSelectedHoldDetail(holdDetail);
        } else {
          setSelectedHoldDetail(null);
        }
      } catch (err) {
        if (!silent) {
          const msg =
            err instanceof ApiError
              ? err.message
              : 'Failed to load student applications.';
          setErrorMessage(msg);
        }
      }
    },
    []
  );

  useEffect(() => {
    fetchClasses(false);
  }, [fetchClasses]);

  useEffect(() => {
    if (studentIdValid) {
      fetchStudentData(studentId, selectedHoldId, false);
    } else {
      setMyHolds([]);
      setSelectedHoldDetail(null);
    }
  }, [studentId, studentIdValid, selectedHoldId, fetchStudentData]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      setNowMs(Date.now());
    }, 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    const pollTimer = window.setInterval(() => {
      fetchClasses(true);
      if (studentIdValid) {
        fetchStudentData(studentId, selectedHoldId, true);
      }
    }, 3000);
    return () => window.clearInterval(pollTimer);
  }, [fetchClasses, fetchStudentData, studentId, studentIdValid, selectedHoldId]);

  useEffect(() => {
    let shouldSync = false;
    for (const hold of myHolds) {
      if (hold.status === 'HELD') {
        const { expired } = formatRemainingTime(hold.expiresAt, nowMs);
        if (expired && !expiredSyncTriggeredRef.current.has(hold.holdId)) {
          expiredSyncTriggeredRef.current.add(hold.holdId);
          shouldSync = true;
        }
      }
    }

    if (shouldSync) {
      fetchClasses(true);
      if (studentIdValid) {
        fetchStudentData(studentId, selectedHoldId, true);
      }
    }
  }, [nowMs, myHolds, fetchClasses, fetchStudentData, studentId, studentIdValid, selectedHoldId]);

  const handleStudentIdSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setSuccessMessage(null);

    if (!studentIdValid) {
      setErrorMessage(
        `Birth Registration Number (X-Student-Id) must be 17 digits. Currently entered: ${studentId.length} digit(s).`
      );
      studentInputRef.current?.focus();
      return;
    }

    await Promise.all([
      fetchClasses(false),
      fetchStudentData(studentId, selectedHoldId, false)
    ]);
    setSuccessMessage(`Student ID ${studentId} (X-Student-Id) is active.`);
  };

  const handleCreateClass = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setSuccessMessage(null);

    const trimmedName = classNameInput.trim();
    const seatsNum = Number(totalSeatsInput);

    if (!trimmedName) {
      setErrorMessage('Please enter a class and shift name (e.g., Class 6 - Morning Shift).');
      return;
    }
    if (!Number.isInteger(seatsNum) || seatsNum <= 0) {
      setErrorMessage('Total seats must be a positive integer greater than 0.');
      return;
    }

    setSubmittingClass(true);
    try {
      const created = await admissionApi.createClass(trimmedName, seatsNum);
      setClassNameInput('');
      setTotalSeatsInput('120');
      setSuccessMessage(`Class "${created.name}" (${created.totalSeats} seats) created.`);
      await fetchClasses(true);
    } catch (err) {
      setErrorMessage(
        err instanceof ApiError ? err.message : 'Failed to create class.'
      );
    } finally {
      setSubmittingClass(false);
    }
  };

  const handleApplyHold = async (cls: SchoolClassItem) => {
    setErrorMessage(null);
    setSuccessMessage(null);

    if (!studentIdValid) {
      setErrorMessage(
        'Please enter a 17-digit Student Birth Registration Number (X-Student-Id) in the top input field before applying.'
      );
      studentInputRef.current?.focus();
      window.scrollTo({ top: 0, behavior: 'smooth' });
      return;
    }

    setBusyActionId(cls.id);
    try {
      const createdHold = await admissionApi.createHold(cls.id, studentId);
      setSelectedHoldId(createdHold.holdId);
      setSuccessMessage(
        `Seat held in "${cls.name}" for Student ID ${studentId}. Pay the admission fee before the timer expires.`
      );
      await Promise.all([
        fetchClasses(true),
        fetchStudentData(studentId, createdHold.holdId, true)
      ]);
    } catch (err) {
      setErrorMessage(
        err instanceof ApiError ? err.message : 'Unable to place seat hold.'
      );
      await fetchClasses(true);
    } finally {
      setBusyActionId(null);
    }
  };

  const handleConfirmHold = async (holdId: string) => {
    setErrorMessage(null);
    setSuccessMessage(null);

    if (!studentIdValid) {
      setErrorMessage('Please enter your 17-digit Student Birth Registration Number.');
      studentInputRef.current?.focus();
      return;
    }

    setBusyActionId(holdId);
    try {
      await admissionApi.confirmHold(holdId, studentId);
      setSuccessMessage('Admission fee paid. Your admission is now CONFIRMED.');
      await Promise.all([
        fetchClasses(true),
        fetchStudentData(studentId, holdId, true)
      ]);
    } catch (err) {
      setErrorMessage(
        err instanceof ApiError ? err.message : 'Failed to confirm admission.'
      );
      await Promise.all([
        fetchClasses(true),
        fetchStudentData(studentId, holdId, true)
      ]);
    } finally {
      setBusyActionId(null);
    }
  };

  const handleCancelHold = async (holdId: string) => {
    setErrorMessage(null);
    setSuccessMessage(null);

    if (!studentIdValid) {
      setErrorMessage('Please enter your 17-digit Student Birth Registration Number.');
      studentInputRef.current?.focus();
      return;
    }

    setBusyActionId(holdId);
    try {
      await admissionApi.cancelHold(holdId, studentId);
      setSuccessMessage('Application cancelled and seat released.');
      await Promise.all([
        fetchClasses(true),
        fetchStudentData(studentId, holdId, true)
      ]);
    } catch (err) {
      setErrorMessage(
        err instanceof ApiError ? err.message : 'Failed to cancel application.'
      );
      await Promise.all([
        fetchClasses(true),
        fetchStudentData(studentId, holdId, true)
      ]);
    } finally {
      setBusyActionId(null);
    }
  };

  const activeHoldView = useMemo(() => {
    if (!studentIdValid) {
      return null;
    }

    const fromList = myHolds.find((h) => h.holdId === selectedHoldId) || myHolds[0] || null;
    if (!fromList) {
      return null;
    }

    const detailMatchesCurrentHold =
      selectedHoldDetail && selectedHoldDetail.holdId === fromList.holdId;

    const holdId = fromList.holdId;
    const classId = fromList.classId;
    const className =
      fromList.className ||
      (detailMatchesCurrentHold ? selectedHoldDetail.className : undefined) ||
      classes.find((c) => c.id === classId)?.name ||
      classId;
    const rawStatus = detailMatchesCurrentHold
      ? selectedHoldDetail.status
      : fromList.status;
    const expiresAt = detailMatchesCurrentHold
      ? selectedHoldDetail.expiresAt
      : fromList.expiresAt;

    const timer = formatRemainingTime(expiresAt, nowMs);
    const effectiveStatus =
      rawStatus === 'HELD' && timer.expired ? 'EXPIRED' : rawStatus;

    return {
      holdId,
      classId,
      className,
      status: effectiveStatus,
      expiresAt,
      timer
    };
  }, [studentIdValid, myHolds, selectedHoldId, selectedHoldDetail, classes, nowMs]);

  const showAdminSection = activeTab === 'all' || activeTab === 'admin';
  const showClassesSection = activeTab === 'all' || activeTab === 'classes';
  const showApplicationSection = activeTab === 'all' || activeTab === 'application';
  const showMyApplicationsSection = activeTab === 'all' || activeTab === 'my-applications';

  return (
    <div className="app-shell">
      <header className="top-header">
        <div className="top-header-inner">
          <div>
            <h1 className="brand-title">School Admission System</h1>
            <p className="brand-subtitle">
              Online First-Come, First-Served Seat Reservation & Admission Portal
            </p>
          </div>

          <form className="student-identity-bar" onSubmit={handleStudentIdSubmit}>
            <label htmlFor="student-id-input" className="student-identity-label">
              Student Birth Registration No. (17 Digits / X-Student-Id)
            </label>
            <div className="student-identity-input-wrap">
              <input
                ref={studentInputRef}
                id="student-id-input"
                type="text"
                inputMode="numeric"
                value={studentId}
                onChange={(e) => updateStudentIdValue(e.target.value)}
                placeholder="Enter 17-digit Birth Reg. No. (e.g. 20152691234567890)"
                className={`student-id-input ${studentIdValid ? 'valid' : ''}`}
              />
              <button
                type="submit"
                className="btn btn-primary btn-sm"
                style={{ whiteSpace: 'nowrap' }}
              >
                Set ID
              </button>
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                style={{ whiteSpace: 'nowrap' }}
                onClick={() => {
                  const sampleId = generateSampleStudentId();
                  updateStudentIdValue(sampleId);
                  setSuccessMessage(`Switched to sample 17-digit Student ID: ${sampleId}`);
                }}
              >
                New Sample ID
              </button>
              {studentId.length > 0 && (
                <button
                  type="button"
                  className="btn btn-secondary btn-sm"
                  onClick={() => {
                    updateStudentIdValue('');
                    setSuccessMessage(null);
                    studentInputRef.current?.focus();
                  }}
                >
                  Clear
                </button>
              )}
            </div>
            <span className={`student-id-hint ${studentIdValid ? 'valid' : ''}`}>
              {studentIdValid
                ? `Active Header — X-Student-Id: ${studentId} (17/17 digits)`
                : `Enter 17 numeric digits (${studentId.length}/17 entered — English or Bangla digits supported)`}
            </span>
          </form>
        </div>
      </header>

      <nav className="nav-bar" aria-label="Main Navigation">
        <div className="nav-bar-inner">
          <button
            type="button"
            className={`nav-tab ${activeTab === 'all' ? 'active' : ''}`}
            onClick={() => setActiveTab('all')}
          >
            All Screens
          </button>
          <button
            type="button"
            className={`nav-tab ${activeTab === 'classes' ? 'active' : ''}`}
            onClick={() => setActiveTab('classes')}
          >
            Class List
          </button>
          <button
            type="button"
            className={`nav-tab ${activeTab === 'application' ? 'active' : ''}`}
            onClick={() => setActiveTab('application')}
          >
            Application Status
          </button>
          <button
            type="button"
            className={`nav-tab ${activeTab === 'my-applications' ? 'active' : ''}`}
            onClick={() => setActiveTab('my-applications')}
          >
            My Applications ({myHolds.length})
          </button>
          <button
            type="button"
            className={`nav-tab ${activeTab === 'admin' ? 'active' : ''}`}
            onClick={() => setActiveTab('admin')}
          >
            Create Class (School Admin)
          </button>
        </div>
      </nav>

      <main className="main-content">
        {errorMessage && (
          <div className="alert-banner alert-error" role="alert">
            <span>{errorMessage}</span>
            <button
              type="button"
              className="alert-close"
              onClick={() => setErrorMessage(null)}
              aria-label="Dismiss error"
            >
              Close
            </button>
          </div>
        )}

        {successMessage && (
          <div className="alert-banner alert-success" role="status">
            <span>{successMessage}</span>
            <button
              type="button"
              className="alert-close"
              onClick={() => setSuccessMessage(null)}
              aria-label="Dismiss message"
            >
              Close
            </button>
          </div>
        )}

        {showAdminSection && (
          <section className="panel" aria-label="Create Class (School Admin)">
            <div className="panel-header">
              <h2 className="panel-title">Create Class (School Admin)</h2>
            </div>
            <div className="panel-body">
              <form className="inline-form" onSubmit={handleCreateClass}>
                <div className="form-field" style={{ flex: 2 }}>
                  <label htmlFor="class-name-input">Class & Shift Name</label>
                  <input
                    id="class-name-input"
                    type="text"
                    className="text-input"
                    placeholder="e.g. Class 6 - Morning Shift"
                    value={classNameInput}
                    onChange={(e) => setClassNameInput(e.target.value)}
                  />
                </div>
                <div className="form-field" style={{ maxWidth: '200px' }}>
                  <label htmlFor="total-seats-input">Total Seats</label>
                  <input
                    id="total-seats-input"
                    type="number"
                    min={1}
                    max={100000}
                    className="text-input"
                    value={totalSeatsInput}
                    onChange={(e) => setTotalSeatsInput(e.target.value)}
                  />
                </div>
                <button
                  type="submit"
                  className="btn btn-primary"
                  disabled={submittingClass}
                >
                  {submittingClass ? 'Creating...' : 'Create Class'}
                </button>
              </form>
            </div>
          </section>
        )}

        {showClassesSection && (
          <section className="panel" aria-label="Class List">
            <div className="panel-header">
              <h2 className="panel-title">Class List</h2>
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                onClick={() => fetchClasses(false)}
              >
                Refresh
              </button>
            </div>
            <div className="table-responsive">
              {loadingClasses ? (
                <div className="empty-notice">Loading classes...</div>
              ) : classes.length === 0 ? (
                <div className="empty-notice">
                  No classes have been created yet. Use the Create Class (School Admin) form to add a class.
                </div>
              ) : (
                <table className="data-table">
                  <thead>
                    <tr>
                      <th>Class Name</th>
                      <th className="num-col">Total Seats</th>
                      <th className="num-col">Held Seats</th>
                      <th className="num-col">Confirmed Seats</th>
                      <th className="num-col">Available Seats</th>
                      <th>Action</th>
                    </tr>
                  </thead>
                  <tbody>
                    {classes.map((cls) => {
                      const existingStudentHold = studentIdValid
                        ? myHolds.find(
                            (h) =>
                              h.classId === cls.id &&
                              (h.status === 'CONFIRMED' ||
                                (h.status === 'HELD' &&
                                  !formatRemainingTime(h.expiresAt, nowMs).expired))
                          )
                        : undefined;

                      const isFull = cls.availableSeats <= 0;
                      const isBusy = busyActionId === cls.id;

                      return (
                        <tr key={cls.id} data-testid={`class-item-${cls.id}`}>
                          <td>
                            <div style={{ fontWeight: 600 }}>{cls.name}</div>
                            <div style={{ fontSize: '0.75rem', color: '#64748b' }}>
                              ID: {cls.id}
                            </div>
                          </td>
                          <td className="num-col" data-testid={`total-seats-${cls.id}`}>
                            {cls.totalSeats}
                          </td>
                          <td className="num-col" data-testid={`held-seats-${cls.id}`}>
                            {cls.heldSeats}
                          </td>
                          <td className="num-col" data-testid={`confirmed-seats-${cls.id}`}>
                            {cls.confirmedSeats}
                          </td>
                          <td
                            className="num-col"
                            style={{
                              fontWeight: 700,
                              color: isFull ? '#dc2626' : '#15803d'
                            }}
                            data-testid={`available-seats-${cls.id}`}
                          >
                            {cls.availableSeats}
                          </td>
                          <td>
                            {existingStudentHold ? (
                              <div className="action-group">
                                <span
                                  className={`status-badge status-${existingStudentHold.status}`}
                                >
                                  {existingStudentHold.status === 'CONFIRMED'
                                    ? 'Confirmed'
                                    : 'Seat Held'}
                                </span>
                                <button
                                  type="button"
                                  className="btn btn-secondary btn-sm"
                                  onClick={() => {
                                    setSelectedHoldId(existingStudentHold.holdId);
                                    if (activeTab === 'classes') {
                                      setActiveTab('application');
                                    } else {
                                      applicationPanelRef.current?.scrollIntoView({
                                        behavior: 'smooth'
                                      });
                                    }
                                  }}
                                >
                                  {existingStudentHold.status === 'HELD'
                                    ? 'Pay / Cancel'
                                    : 'View Status'}
                                </button>
                              </div>
                            ) : (
                              <button
                                type="button"
                                className="btn btn-primary btn-sm"
                                disabled={isFull || isBusy}
                                onClick={() => handleApplyHold(cls)}
                              >
                                {isBusy
                                  ? 'Applying...'
                                  : isFull
                                  ? 'No Seats'
                                  : 'Apply'}
                              </button>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )}
            </div>
          </section>
        )}

        {showApplicationSection && (
          <section
            ref={applicationPanelRef}
            className="panel"
            aria-label="Application Status"
          >
            <div className="panel-header">
              <h2 className="panel-title">Application Status</h2>
            </div>
            <div className="panel-body">
              {!studentIdValid ? (
                <div className="empty-notice">
                  Enter a valid 17-digit Student Birth Registration Number above to view or manage your application.
                </div>
              ) : !activeHoldView ? (
                <div className="empty-notice">
                  No active application for Student ID {studentId}. Click "Apply" on an available class in the Class List.
                </div>
              ) : (
                <div data-testid="active-hold-panel">
                  <div className="application-detail-grid">
                    <div className="detail-item">
                      <span className="detail-label">Student ID (X-Student-Id)</span>
                      <span className="detail-value" style={{ fontFamily: 'monospace' }}>
                        {studentId}
                      </span>
                    </div>
                    <div className="detail-item">
                      <span className="detail-label">Class Name</span>
                      <span className="detail-value">{activeHoldView.className}</span>
                    </div>
                    <div className="detail-item">
                      <span className="detail-label">Hold ID</span>
                      <span className="detail-value">{activeHoldView.holdId}</span>
                    </div>
                    <div className="detail-item">
                      <span className="detail-label">Status</span>
                      <div>
                        <span
                          className={`status-badge status-${activeHoldView.status}`}
                          data-testid="active-hold-status"
                        >
                          {activeHoldView.status}
                        </span>
                      </div>
                    </div>
                    <div className="detail-item">
                      <span className="detail-label">Time Remaining</span>
                      {activeHoldView.status === 'HELD' ? (
                        <span
                          className="timer-value"
                          data-testid="active-hold-countdown"
                        >
                          {activeHoldView.timer.formatted}
                        </span>
                      ) : (
                        <span className="detail-value" style={{ color: '#64748b' }}>
                          {activeHoldView.status === 'EXPIRED'
                            ? '00:00 (Expired)'
                            : 'N/A'}
                        </span>
                      )}
                    </div>
                  </div>

                  <div className="action-group">
                    <button
                      type="button"
                      className="btn btn-success"
                      data-testid="confirm-hold-btn"
                      disabled={
                        activeHoldView.status !== 'HELD' ||
                        activeHoldView.timer.expired ||
                        busyActionId === activeHoldView.holdId
                      }
                      onClick={() => handleConfirmHold(activeHoldView.holdId)}
                    >
                      Pay Admission Fee
                    </button>
                    <button
                      type="button"
                      className="btn btn-danger"
                      data-testid="cancel-hold-btn"
                      disabled={
                        activeHoldView.status !== 'HELD' ||
                        activeHoldView.timer.expired ||
                        busyActionId === activeHoldView.holdId
                      }
                      onClick={() => handleCancelHold(activeHoldView.holdId)}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              )}
            </div>
          </section>
        )}

        {showMyApplicationsSection && (
          <section className="panel" aria-label="My Applications">
            <div className="panel-header">
              <h2 className="panel-title">My Applications</h2>
            </div>
            <div className="table-responsive">
              {!studentIdValid ? (
                <div className="empty-notice">
                  Enter your 17-digit Student Birth Registration Number above to view all of your applications.
                </div>
              ) : myHolds.length === 0 ? (
                <div className="empty-notice">
                  No applications found for Student ID {studentId}.
                </div>
              ) : (
                <table className="data-table">
                  <thead>
                    <tr>
                      <th>Hold ID</th>
                      <th>Class Name</th>
                      <th>Status</th>
                      <th>Time Remaining / Expires At</th>
                      <th>Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {myHolds.map((item) => {
                      const timer = formatRemainingTime(item.expiresAt, nowMs);
                      const effectiveStatus =
                        item.status === 'HELD' && timer.expired
                          ? 'EXPIRED'
                          : item.status;
                      const isSelected = activeHoldView?.holdId === item.holdId;

                      return (
                        <tr
                          key={item.holdId}
                          className={isSelected ? 'selected-row' : ''}
                          data-testid={`my-hold-${item.holdId}`}
                          onClick={() => setSelectedHoldId(item.holdId)}
                          style={{ cursor: 'pointer' }}
                        >
                          <td style={{ fontFamily: 'monospace' }}>{item.holdId}</td>
                          <td style={{ fontWeight: 600 }}>
                            {item.className || item.classId}
                          </td>
                          <td>
                            <span
                              className={`status-badge status-${effectiveStatus}`}
                              data-testid={`my-hold-status-${item.holdId}`}
                            >
                              {effectiveStatus}
                            </span>
                          </td>
                          <td>
                            {effectiveStatus === 'HELD' ? (
                              <span style={{ fontWeight: 600, color: '#b45309' }}>
                                {timer.formatted} remaining
                              </span>
                            ) : (
                              <span style={{ color: '#64748b', fontSize: '0.82rem' }}>
                                {new Date(item.expiresAt).toLocaleString()}
                              </span>
                            )}
                          </td>
                          <td>
                            <div className="action-group">
                              {effectiveStatus === 'HELD' ? (
                                <>
                                  <button
                                    type="button"
                                    className="btn btn-success btn-sm"
                                    disabled={busyActionId === item.holdId}
                                    onClick={(e) => {
                                      e.stopPropagation();
                                      handleConfirmHold(item.holdId);
                                    }}
                                  >
                                    Pay Admission Fee
                                  </button>
                                  <button
                                    type="button"
                                    className="btn btn-danger btn-sm"
                                    disabled={busyActionId === item.holdId}
                                    onClick={(e) => {
                                      e.stopPropagation();
                                      handleCancelHold(item.holdId);
                                    }}
                                  >
                                    Cancel
                                  </button>
                                </>
                              ) : (
                                <button
                                  type="button"
                                  className="btn btn-secondary btn-sm"
                                  onClick={(e) => {
                                    e.stopPropagation();
                                    setSelectedHoldId(item.holdId);
                                  }}
                                >
                                  View Details
                                </button>
                              )}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )}
            </div>
          </section>
        )}
      </main>
    </div>
  );
}

export default App;
