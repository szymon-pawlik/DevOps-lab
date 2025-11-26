import { useState, useEffect, useRef, useCallback } from 'react';
import axios from 'axios';
import * as signalR from '@microsoft/signalr';
import { authService, type User } from './auth';
import Login from './Login';
import './App.scss';

interface Job {
  id: string;
  text: string;
  processedText?: string;
  status: number; // 0=Pending, 1=Processing, 2=Completed, 3=Failed
  type?: number; // 0=Uppercase, 1=Lowercase, 2=Reverse, 3=CountWords, 4=Translate
  createdAt: string;
  processedAt?: string;
}

type JobType = 'uppercase' | 'lowercase' | 'reverse' | 'countwords' | 'translate';

const JOB_TYPES: { value: number; label: string; type: JobType }[] = [
  { value: 0, label: 'Uppercase', type: 'uppercase' },
  { value: 1, label: 'Lowercase', type: 'lowercase' },
  { value: 2, label: 'Reverse', type: 'reverse' },
  { value: 3, label: 'Count Words', type: 'countwords' },
  { value: 4, label: 'Translate', type: 'translate' }
];

// API URL - in browser always use localhost (ports are mapped in docker-compose)
const API_URL = import.meta.env.VITE_API_URL || 'http://localhost:8080';

// Configure axios to include JWT token
axios.interceptors.request.use((config) => {
  const token = authService.getToken();
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

axios.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401 || error.response?.status === 500) {
      // If 500 error with "User not found" or foreign key constraint, it might be token issue
      const errorMessage = error.response?.data?.toString().toLowerCase() || '';
      if (error.response?.status === 500 && (errorMessage.includes('user not found') || errorMessage.includes('foreign key'))) {
        console.warn('Token may be invalid, clearing auth...');
        authService.clearAuth();
        window.location.reload();
        return Promise.reject(error);
      }
      if (error.response?.status === 401) {
        authService.clearAuth();
        window.location.reload();
      }
    }
    return Promise.reject(error);
  }
);

function App() {
  const [user, setUser] = useState<User | null>(authService.getUser());
  const [text, setText] = useState('');
  const [jobType, setJobType] = useState<number>(0); // Default to Uppercase
  const [jobs, setJobs] = useState<Job[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [apiStatus, setApiStatus] = useState<'checking' | 'online' | 'offline'>('checking');
  const [wsConnected, setWsConnected] = useState(false);
  const connectionRef = useRef<signalR.HubConnection | null>(null);

  const formatTranslatedText = (text: string) => {
    // Parse [UNTRANSLATED:word] markers and [Brak możliwości tłumaczenia: ...] section
    const parts: (string | React.ReactElement)[] = [];
    let currentIndex = 0;
    
    // Find untranslated words markers
    const untranslatedRegex = /\[UNTRANSLATED:([^\]]+)\]/g;
    let match;
    const untranslatedWords: string[] = [];
    
    while ((match = untranslatedRegex.exec(text)) !== null) {
      const before = text.substring(currentIndex, match.index);
      if (before) {
        parts.push(before);
      }
      parts.push(
        <span key={match.index} className="untranslated-word">
          {match[1]}
        </span>
      );
      untranslatedWords.push(match[1]);
      currentIndex = match.index + match[0].length;
    }
    
    if (currentIndex < text.length) {
      const remaining = text.substring(currentIndex);
      // Remove the "| [Brak możliwości tłumaczenia: ...]" part if it exists
      const untranslatedSectionRegex = /\s*\|\s*\[Brak możliwości tłumaczenia:[^\]]+\]/;
      const cleaned = remaining.replace(untranslatedSectionRegex, '');
      if (cleaned) {
        parts.push(cleaned);
      }
    }
    
    // Add warning about untranslated words if any
    if (untranslatedWords.length > 0) {
      const uniqueWords = [...new Set(untranslatedWords)];
      parts.push(
        <span key="warning" className="translation-warning">
          {' '}(Brak możliwości tłumaczenia: {uniqueWords.join(', ')})
        </span>
      );
    }
    
    return parts.length > 0 ? <>{parts}</> : text;
  };

  const checkApiStatus = useCallback(async () => {
    try {
      const response = await axios.get(`${API_URL}/api/job/health`);
      setApiStatus(response.status === 200 ? 'online' : 'offline');
    } catch {
      setApiStatus('offline');
    }
  }, []);

  const fetchJobsRef = useRef<(() => Promise<void>) | null>(null);
  
  // Update fetchJobs function when user changes
  useEffect(() => {
    fetchJobsRef.current = async () => {
      if (!user) return;
      try {
        const response = await axios.get<Job[]>(`${API_URL}/api/job`);
        setJobs(response.data);
      } catch (err) {
        console.error('Failed to fetch jobs:', err);
      }
    };
  }, [user]);

  const handleLogout = useCallback(() => {
    // Stop SignalR connection
    if (connectionRef.current) {
      connectionRef.current.stop().catch(console.error);
      connectionRef.current = null;
    }
    
    // Clear all state first
    setJobs([]);
    setText('');
    setError(null);
    setApiStatus('checking');
    setWsConnected(false);
    
    // Clear auth and user last to trigger re-render
    authService.clearAuth();
    setUser(null);
  }, []);

  const handleSubmit = useCallback(async (e: React.FormEvent) => {
    e.preventDefault();
    const jobText = (e.target as HTMLFormElement).querySelector('textarea')?.value || text;
    if (!jobText.trim()) return;

    setLoading(true);
    setError(null);

    try {
      const token = authService.getToken();
      console.log('Submitting job with token:', token ? 'Token exists' : 'No token');
      console.log('Job text:', jobText);
      
      const response = await axios.post(`${API_URL}/api/job`, {
        text: jobText.trim(),
        type: jobType
      });

      console.log('Job submitted successfully:', response.data);
      setText('');
      // Jobs will be updated via SignalR, but fetch once to be sure
      setTimeout(() => {
        if (fetchJobsRef.current) {
          fetchJobsRef.current();
        }
      }, 500);
    } catch (err: any) {
      console.error('Error submitting job:', err);
      console.error('Error response:', err.response);
      const errorMessage = err.response?.data?.message || err.response?.data || err.message || 'Failed to submit job';
      setError(errorMessage);
    } finally {
      setLoading(false);
    }
  }, [text]);

  // Setup SignalR connection
  useEffect(() => {
    if (!user) return;

    let isMounted = true;
    const hubUrl = `${API_URL}/jobhub`;
    const token = authService.getToken();
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => token || ''
      })
      .withAutomaticReconnect()
      .build();

    connectionRef.current = connection;

    connection.on('JobCreated', (job: { id: string; text: string; type?: number; status: number; createdAt: string }) => {
      if (!isMounted) return;
      console.log('JobCreated received:', job);
      if (!job || !job.id) {
        console.error('Invalid job data received:', job);
        return;
      }
      setJobs(prev => {
        // Check if job already exists
        const exists = prev.find(j => j.id === job.id);
        if (exists) {
          console.log('Job already exists, skipping:', job.id);
          return prev;
        }
                  const newJob = {
                    id: job.id || '',
                    text: job.text || '',
                    type: job.type ?? 0,
                    status: job.status ?? 0,
                    createdAt: job.createdAt || new Date().toISOString()
                  };
        console.log('Adding new job:', newJob);
        return [newJob, ...prev];
      });
    });

    connection.on('JobUpdated', (update: { id: string; status: number; processedText?: string; processedAt?: string }) => {
      if (!isMounted) return;
      console.log('JobUpdated received:', update);
      if (!update || !update.id) {
        console.error('Invalid update data received:', update);
        return;
      }
      setJobs(prev => {
        console.log('Updating jobs, current jobs:', prev);
        const updated = prev.map(job => 
          job && job.id === update.id 
            ? { ...job, status: update.status ?? job.status, processedText: update.processedText, processedAt: update.processedAt }
            : job
        ).filter(job => job !== null && job !== undefined);
        console.log('Updated jobs:', updated);
        return updated;
      });
    });

    connection.start()
      .then(() => {
        if (isMounted) {
          setWsConnected(true);
          console.log('SignalR connected');
        }
      })
      .catch(err => {
        if (isMounted) {
          console.error('SignalR connection error:', err);
          setWsConnected(false);
        }
      });

    connection.onreconnecting(() => {
      if (isMounted) {
        setWsConnected(false);
      }
    });

    connection.onreconnected(() => {
      if (isMounted) {
        setWsConnected(true);
      }
    });

    return () => {
      isMounted = false;
      if (connectionRef.current) {
        connectionRef.current.stop().catch(console.error);
        connectionRef.current = null;
      }
    };
  }, [user]);

  // Check API status and fetch jobs on mount
  useEffect(() => {
    if (!user) return;
    
    checkApiStatus();
    if (fetchJobsRef.current) {
      fetchJobsRef.current();
    }
    
    const statusInterval = setInterval(() => {
      checkApiStatus();
    }, 30000); // Check every 30s
    
    return () => {
      clearInterval(statusInterval);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user]);

  // Early return for login screen - MUST be after all hooks
  if (!user) {
    return <Login key="login" onLogin={(user) => setUser(user)} />;
  }

  return (
    <div className="app">
      <header className="header">
        <div className="header-content">
          <h1>Producer-Consumer System</h1>
          <div className="user-info">
            <span className="username">{user.username}</span>
            {user.role === 'Admin' && <span className="role-badge">Admin</span>}
            <button onClick={handleLogout} className="logout-btn">Logout</button>
          </div>
        </div>
        <div className="status-group">
          <div className={`status ${apiStatus}`}>
            <span className="status-dot"></span>
            <span>API: {apiStatus === 'online' ? 'Online' : apiStatus === 'offline' ? 'Offline' : 'Checking...'}</span>
          </div>
          <div className={`status ${wsConnected ? 'online' : 'offline'}`}>
            <span className="status-dot"></span>
            <span>WebSocket: {wsConnected ? 'Connected' : 'Disconnected'}</span>
          </div>
        </div>
      </header>

      <main className="main">
        <section className="submit-section">
          <h2>Submit New Job</h2>
          <form onSubmit={handleSubmit} className="job-form">
            <div className="form-group">
              <label htmlFor="job-type">Job Type:</label>
              <select
                id="job-type"
                value={jobType}
                onChange={(e) => setJobType(Number(e.target.value))}
                disabled={loading || apiStatus === 'offline'}
                className="job-type-select"
              >
                {JOB_TYPES.map((type) => (
                  <option key={type.value} value={type.value}>
                    {type.label}
                  </option>
                ))}
              </select>
            </div>
            <div className="form-group">
              <label htmlFor="job-text">Text to Process:</label>
              <textarea
                id="job-text"
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder={`Enter text to process (will be ${JOB_TYPES.find(t => t.value === jobType)?.label.toLowerCase() || 'processed'})...`}
                rows={4}
                disabled={loading || apiStatus === 'offline'}
              />
            </div>
            <button 
              type="submit" 
              disabled={loading || !text.trim() || apiStatus === 'offline'}
              className="submit-btn"
            >
              {loading ? 'Submitting...' : 'Submit Job'}
            </button>
          </form>
          {error && <div className="error-message">{error}</div>}
        </section>

        <section className="jobs-section">
          <h2>Job History</h2>
          {jobs.length === 0 ? (
            <div className="empty-state">No jobs submitted yet</div>
          ) : (
            <div className="jobs-list">
              {jobs.map((job) => {
                if (!job || !job.id) return null;
                return (
                          <div key={job.id} className={`job-card ${job.status}`}>
                            <div className="job-header">
                              <span className="job-id">{job.id?.substring ? job.id.substring(0, 8) : job.id}...</span>
                              <span className="job-type-badge">
                                {JOB_TYPES.find(t => t.value === (job.type ?? 0))?.label || 'Unknown'}
                              </span>
                              <span className={`job-status status-${job.status}`}>
                                {job.status === 0 ? 'pending' : job.status === 1 ? 'processing' : job.status === 2 ? 'completed' : 'failed'}
                              </span>
                            </div>
                    <div className="job-content">
                      <div className="job-text">
                        <strong>Original:</strong> {job.text || 'N/A'}
                      </div>
                      {job.status === 2 && job.processedText && (
                        <div className="job-result">
                          <strong>Processed:</strong>{' '}
                          {job.type === 4 ? (
                            <span className="translated-text">
                              {formatTranslatedText(job.processedText)}
                            </span>
                          ) : (
                            job.processedText
                          )}
                        </div>
                      )}
                      {job.status === 3 && (
                        <div className="job-result error">
                          <strong>Error:</strong> Job processing failed
                        </div>
                      )}
                      <div className="job-time">
                        {job.createdAt ? new Date(job.createdAt).toLocaleString() : 'N/A'}
                        {job.processedAt && ` • Processed: ${new Date(job.processedAt).toLocaleString()}`}
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </section>
      </main>
    </div>
  );
}

export default App;
