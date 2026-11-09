import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import App from '../App';
import { admissionApi } from '../api/client';

vi.mock('../api/client', async () => {
  const actual = await vi.importActual<typeof import('../api/client')>('../api/client');
  return {
    ...actual,
    admissionApi: {
      createClass: vi.fn(),
      getClasses: vi.fn(),
      getClassById: vi.fn(),
      createHold: vi.fn(),
      getHold: vi.fn(),
      confirmHold: vi.fn(),
      cancelHold: vi.fn(),
      getMyHolds: vi.fn()
    }
  };
});

describe('School Admission Frontend UI', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders classes table with seat breakdown and persists 17-digit student ID in localStorage', async () => {
    vi.mocked(admissionApi.getClasses).mockResolvedValue([
      {
        id: 'c_123',
        name: 'Class 6 - Morning Shift',
        totalSeats: 120,
        heldSeats: 15,
        confirmedSeats: 80,
        availableSeats: 25
      }
    ]);
    vi.mocked(admissionApi.getMyHolds).mockResolvedValue([]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Class 6 - Morning Shift')).toBeInTheDocument();
    });

    expect(screen.getByTestId('total-seats-c_123')).toHaveTextContent('120');
    expect(screen.getByTestId('held-seats-c_123')).toHaveTextContent('15');
    expect(screen.getByTestId('confirmed-seats-c_123')).toHaveTextContent('80');
    expect(screen.getByTestId('available-seats-c_123')).toHaveTextContent('25');

    const input = screen.getByLabelText(/Student Birth Registration No/i);
    fireEvent.change(input, { target: { value: '20152691234567890' } });

    expect(localStorage.getItem('school_admission_student_id')).toBe('20152691234567890');
    await waitFor(() => {
      expect(admissionApi.getMyHolds).toHaveBeenCalledWith('20152691234567890');
    });
  });

  it('automatically transitions HELD application to EXPIRED and disables payment button when countdown reaches 0 without page reload', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const startNow = new Date('2026-11-01T04:00:00Z').getTime();
    vi.setSystemTime(startNow);

    localStorage.setItem('school_admission_student_id', '20152691234567890');

    const expiresIso = new Date(startNow + 3000).toISOString();

    vi.mocked(admissionApi.getClasses).mockResolvedValue([
      {
        id: 'c_123',
        name: 'Class 6 - Morning Shift',
        totalSeats: 120,
        heldSeats: 1,
        confirmedSeats: 0,
        availableSeats: 119
      }
    ]);
    vi.mocked(admissionApi.getMyHolds).mockResolvedValue([
      {
        holdId: 'h_789',
        classId: 'c_123',
        className: 'Class 6 - Morning Shift',
        status: 'HELD',
        expiresAt: expiresIso
      }
    ]);
    vi.mocked(admissionApi.getHold).mockResolvedValue({
      holdId: 'h_789',
      classId: 'c_123',
      className: 'Class 6 - Morning Shift',
      status: 'HELD',
      expiresAt: expiresIso
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('active-hold-status')).toHaveTextContent('HELD');
    });

    const confirmBtn = screen.getByTestId('confirm-hold-btn');
    expect(confirmBtn).not.toBeDisabled();

    act(() => {
      vi.advanceTimersByTime(4000);
    });

    await waitFor(() => {
      expect(screen.getByTestId('active-hold-status')).toHaveTextContent('EXPIRED');
    });
    expect(confirmBtn).toBeDisabled();
  });

  it('shows a user-friendly error banner when seat hold fails with 409 Conflict', async () => {
    const { ApiError } = await import('../api/client');
    localStorage.setItem('school_admission_student_id', '20152691234567890');

    vi.mocked(admissionApi.getClasses).mockResolvedValue([
      {
        id: 'c_123',
        name: 'Class 6 - Morning Shift',
        totalSeats: 10,
        heldSeats: 9,
        confirmedSeats: 0,
        availableSeats: 1
      }
    ]);
    vi.mocked(admissionApi.getMyHolds).mockResolvedValue([]);
    vi.mocked(admissionApi.createHold).mockRejectedValue(
      new ApiError('No seats are currently available in this class.', 409, 'NO_SEATS_AVAILABLE')
    );

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Apply')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Apply'));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'No seats are currently available in this class.'
      );
    });
  });
});
