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
  priority?: number; // 0=Low, 1=Normal, 2=High, 3=Critical
  createdAt: string;
  processedAt?: string;
  originalFileName?: string;
  processedFileName?: string;
  fileSize?: number;
  hasFile?: boolean;
  hasProcessedFile?: boolean;
}

interface Statistics {
  totalJobs: number;
  completed: number;
  pending: number;
  processing: number;
  failed: number;
  jobsByType: Array<{ type: number; count: number }>;
  jobsByPriority: Array<{ priority: number; count: number }>;
  averageProcessingTimeSeconds: number;
  recentJobs: Array<{
    id: string;
    type: number;
    priority: number;
    status: number;
    createdAt: string;
  }>;
}

type JobType = 'uppercase' | 'lowercase' | 'reverse' | 'countwords' | 'translate';

const JOB_TYPES: { value: number; label: string; type: JobType }[] = [
  { value: 0, label: 'Wielkie litery', type: 'uppercase' },
  { value: 1, label: 'Małe litery', type: 'lowercase' },
  { value: 2, label: 'Odwróć', type: 'reverse' },
  { value: 3, label: 'Policz słowa', type: 'countwords' },
  { value: 4, label: 'Tłumacz', type: 'translate' }
];

const JOB_PRIORITIES: { value: number; label: string; color: string }[] = [
  { value: 0, label: 'Niski', color: '#6c757d' },
  { value: 1, label: 'Normalny', color: '#0d6efd' },
  { value: 2, label: 'Wysoki', color: '#ffc107' },
  { value: 3, label: 'Krytyczny', color: '#dc3545' }
];

// API URL - use relative path, nginx will proxy to Producer API
const API_URL = import.meta.env.VITE_API_URL || '';

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
  const [jobPriority, setJobPriority] = useState<number>(1); // Default to Normal
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [statistics, setStatistics] = useState<Statistics | null>(null);
  const [showStatistics, setShowStatistics] = useState(false);
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
    // Stop SignalR connection properly
    if (connectionRef.current) {
      connectionRef.current.stop().catch((err) => {
        console.error('Error stopping SignalR:', err);
      });
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

  const handleFileChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) {
      if (file.size > 10 * 1024 * 1024) {
        setError('Rozmiar pliku przekracza limit 10MB');
        return;
      }
      setSelectedFile(file);
      setText(''); // Clear text when file is selected
    }
  }, []);

  const handleSubmit = useCallback(async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);

    try {
      if (selectedFile) {
        // Upload file
        const formData = new FormData();
        formData.append('file', selectedFile);
        formData.append('type', jobType.toString());
        formData.append('priority', jobPriority.toString());

        await axios.post(`${API_URL}/api/job/upload`, formData, {
          headers: {
            'Content-Type': 'multipart/form-data'
          }
        });

        setSelectedFile(null);
      } else {
        // Submit text job
        const jobText = text.trim();
        if (!jobText) {
          setError('Wymagany jest tekst lub plik');
          setLoading(false);
          return;
        }

        await axios.post(`${API_URL}/api/job`, {
          text: jobText,
          type: jobType,
          priority: jobPriority
        });

        setText('');
      }

      // Jobs will be updated via SignalR, but fetch once to be sure
      setTimeout(() => {
        if (fetchJobsRef.current) {
          fetchJobsRef.current();
        }
        fetchStatistics();
      }, 500);
    } catch (err: any) {
      console.error('Error submitting job:', err);
      const errorMessage = err.response?.data?.message || err.response?.data || err.message || 'Nie udało się utworzyć zadania';
      setError(errorMessage);
    } finally {
      setLoading(false);
    }
  }, [text, selectedFile, jobType, jobPriority]);

  const handleDownload = useCallback(async (jobId: string) => {
    try {
      const response = await axios.get(`${API_URL}/api/job/${jobId}/download`, {
        responseType: 'blob'
      });
      
      const url = window.URL.createObjectURL(new Blob([response.data]));
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', `result_${jobId}.txt`);
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.URL.revokeObjectURL(url);
    } catch (err: any) {
      console.error('Error downloading result:', err);
      setError('Nie udało się pobrać wyniku');
    }
  }, []);

  const fetchStatistics = useCallback(async () => {
    if (!user) return;
    try {
      const response = await axios.get<Statistics>(`${API_URL}/api/job/statistics`);
      setStatistics(response.data);
    } catch (err) {
      console.error('Failed to fetch statistics:', err);
    }
  }, [user]);

  const handleExport = useCallback(async (format: 'csv' | 'json' = 'csv') => {
    try {
      const response = await axios.get(`${API_URL}/api/job/export?format=${format}`, {
        responseType: 'blob'
      });
      
      const url = window.URL.createObjectURL(new Blob([response.data]));
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', `jobs_export_${new Date().toISOString().slice(0, 10)}.${format}`);
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.URL.revokeObjectURL(url);
    } catch (err: any) {
      console.error('Error exporting jobs:', err);
      setError('Nie udało się wyeksportować zadań');
    }
  }, []);

  // Setup SignalR connection
  useEffect(() => {
    if (!user) {
      // Ensure connection is stopped when user logs out
      if (connectionRef.current) {
        const conn = connectionRef.current;
        connectionRef.current = null; // Clear reference first
        conn.stop().catch((err) => {
          console.error('Error stopping SignalR on logout:', err);
        });
      }
      return;
    }

    let isMounted = true;
    // Use relative path - nginx will proxy to Producer API
    // If API_URL is empty, use relative path (works with nginx proxy)
    const hubUrl = API_URL ? `${API_URL}/jobhub` : '/jobhub';
    const token = authService.getToken();
    
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => token || '',
        transport: signalR.HttpTransportType.LongPolling // Use Long Polling for port-forward compatibility
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.elapsedMilliseconds < 60000) {
            return 2000; // Retry after 2 seconds for first minute
          }
          return 10000; // Then retry every 10 seconds
        }
      })
      .build();

    connectionRef.current = connection;

    connection.on('JobCreated', (job: { id: string; text: string; type?: number; priority?: number; status: number; createdAt: string; hasFile?: boolean; fileName?: string }) => {
      if (!isMounted) return;
      if (!job || !job.id) {
        console.error('Invalid job data received:', job);
        return;
      }
      setJobs(prev => {
        // Check if job already exists
        const exists = prev.find(j => j.id === job.id);
        if (exists) {
          return prev;
        }
        const newJob: Job = {
          id: job.id || '',
          text: job.text || '',
          type: job.type ?? 0,
          priority: job.priority ?? 1,
          status: job.status ?? 0,
          createdAt: job.createdAt || new Date().toISOString(),
          hasFile: job.hasFile || false,
          originalFileName: job.fileName
        };
        return [newJob, ...prev];
      });
      if (isMounted) {
        fetchStatistics();
      }
    });

    connection.on('JobUpdated', (update: { id: string; status: number; processedText?: string; processedAt?: string }) => {
      if (!isMounted) return;
      if (!update || !update.id) {
        console.error('Invalid update data received:', update);
        return;
      }
      setJobs(prev => {
        const updated = prev.map(job => 
          job && job.id === update.id 
            ? { ...job, status: update.status ?? job.status, processedText: update.processedText, processedAt: update.processedAt }
            : job
        ).filter(job => job !== null && job !== undefined);
        return updated;
      });
    });

    connection.start()
      .then(() => {
        if (isMounted) {
          setWsConnected(true);
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
      // Cleanup: stop connection when component unmounts or user changes
      if (connectionRef.current) {
        const conn = connectionRef.current;
        connectionRef.current = null; // Clear reference first to prevent reconnection attempts
        conn.stop().catch((err) => {
          console.error('Error during SignalR cleanup:', err);
        });
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
    fetchStatistics();
    
    const statusInterval = setInterval(() => {
      checkApiStatus();
      fetchStatistics();
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
          <h1>TextFlow</h1>
          <div className="user-info">
            <span className="username">{user.username}</span>
            {user.role === 'Admin' && <span className="role-badge">Admin</span>}
            <button onClick={handleLogout} className="logout-btn">Wyloguj</button>
          </div>
        </div>
        <div className="status-group">
          <div className={`status ${apiStatus}`}>
            <span className="status-dot"></span>
            <span>API: {apiStatus === 'online' ? 'Online' : apiStatus === 'offline' ? 'Offline' : 'Sprawdzanie...'}</span>
          </div>
          <div className={`status ${wsConnected ? 'online' : 'offline'}`}>
            <span className="status-dot"></span>
            <span>WebSocket: {wsConnected ? 'Połączono' : 'Rozłączono'}</span>
          </div>
        </div>
      </header>

      <main className="main">
        <section className="submit-section">
          <div className="section-header">
            <h2>Utwórz nowe zadanie</h2>
            <div className="action-buttons">
              <button 
                onClick={() => setShowStatistics(!showStatistics)} 
                className="btn-secondary"
              >
                {showStatistics ? 'Ukryj' : 'Pokaż'} Statystyki
              </button>
              <button 
                onClick={() => handleExport('csv')} 
                className="btn-secondary"
                disabled={jobs.length === 0}
              >
                Eksportuj CSV
              </button>
              <button 
                onClick={() => handleExport('json')} 
                className="btn-secondary"
                disabled={jobs.length === 0}
              >
                Eksportuj JSON
              </button>
            </div>
          </div>

          {showStatistics && statistics && (
            <div className="statistics-panel">
              <h3>Statystyki</h3>
              <div className="stats-grid">
                <div className="stat-card">
                  <div className="stat-value">{statistics.totalJobs}</div>
                  <div className="stat-label">Wszystkie zadania</div>
                </div>
                <div className="stat-card">
                  <div className="stat-value">{statistics.completed}</div>
                  <div className="stat-label">Ukończone</div>
                </div>
                <div className="stat-card">
                  <div className="stat-value">{statistics.pending}</div>
                  <div className="stat-label">Oczekujące</div>
                </div>
                <div className="stat-card">
                  <div className="stat-value">{statistics.processing}</div>
                  <div className="stat-label">Przetwarzane</div>
                </div>
                <div className="stat-card">
                  <div className="stat-value">{statistics.failed}</div>
                  <div className="stat-label">Nieudane</div>
                </div>
                <div className="stat-card">
                  <div className="stat-value">{Math.round(statistics.averageProcessingTimeSeconds)}s</div>
                  <div className="stat-label">Śr. czas przetwarzania</div>
                </div>
              </div>
            </div>
          )}

          <form onSubmit={handleSubmit} className="job-form">
            <div className="form-group">
              <label htmlFor="job-type">Typ zadania:</label>
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
              <label htmlFor="job-priority">Priorytet:</label>
              <select
                id="job-priority"
                value={jobPriority}
                onChange={(e) => setJobPriority(Number(e.target.value))}
                disabled={loading || apiStatus === 'offline'}
                className="job-priority-select"
              >
                {JOB_PRIORITIES.map((priority) => (
                  <option key={priority.value} value={priority.value}>
                    {priority.label}
                  </option>
                ))}
              </select>
            </div>
            <div className="form-group">
              <label htmlFor="file-upload">Prześlij plik (lub wpisz tekst poniżej):</label>
              <input
                id="file-upload"
                type="file"
                onChange={handleFileChange}
                disabled={loading || apiStatus === 'offline'}
                accept=".txt,.csv"
              />
              {selectedFile && (
                <div className="file-info">
                  <span>{selectedFile.name}</span>
                  <span className="file-size">({(selectedFile.size / 1024).toFixed(2)} KB)</span>
                  <button 
                    type="button" 
                    onClick={() => setSelectedFile(null)}
                    className="btn-remove-file"
                  >
                    Usuń
                  </button>
                </div>
              )}
            </div>
            <div className="form-group">
              <label htmlFor="job-text">Tekst do przetworzenia:</label>
              <textarea
                id="job-text"
                value={text}
                onChange={(e) => {
                  setText(e.target.value);
                  setSelectedFile(null); // Clear file when text is entered
                }}
                placeholder={`Wpisz tekst do przetworzenia (zostanie ${JOB_TYPES.find(t => t.value === jobType)?.label.toLowerCase() || 'przetworzony'})...`}
                rows={4}
                disabled={loading || apiStatus === 'offline' || selectedFile !== null}
              />
            </div>
            <button 
              type="submit" 
              disabled={loading || (!text.trim() && !selectedFile) || apiStatus === 'offline'}
              className="submit-btn"
            >
              {loading ? 'Przesyłanie...' : selectedFile ? 'Prześlij plik' : 'Utwórz zadanie'}
            </button>
          </form>
          {error && <div className="error-message">{error}</div>}
        </section>

        <section className="jobs-section">
          <h2>Historia zadań</h2>
          {jobs.length === 0 ? (
            <div className="empty-state">Brak zadań</div>
          ) : (
            <div className="jobs-list">
              {jobs.map((job) => {
                if (!job || !job.id) return null;
                return (
                          <div key={job.id} className={`job-card ${job.status}`}>
                            <div className="job-header">
                              <span className="job-id">{job.id?.substring ? job.id.substring(0, 8) : job.id}...</span>
                              <span className="job-type-badge">
                                {JOB_TYPES.find(t => t.value === (job.type ?? 0))?.label || 'Nieznany'}
                              </span>
                              {job.priority !== undefined && (
                                <span 
                                  className="job-priority-badge"
                                  style={{ 
                                    backgroundColor: JOB_PRIORITIES.find(p => p.value === job.priority)?.color || '#6c757d',
                                    color: 'white'
                                  }}
                                >
                                  {JOB_PRIORITIES.find(p => p.value === job.priority)?.label || 'Normalny'}
                                </span>
                              )}
                              <span className={`job-status status-${job.status}`}>
                                {job.status === 0 ? 'oczekujące' : job.status === 1 ? 'przetwarzanie' : job.status === 2 ? 'ukończone' : 'nieudane'}
                              </span>
                            </div>
                    <div className="job-content">
                      {job.hasFile && job.originalFileName && (
                        <div className="job-file-info">
                          <strong>Plik:</strong> {job.originalFileName}
                          {job.fileSize && ` (${(job.fileSize / 1024).toFixed(2)} KB)`}
                        </div>
                      )}
                      <div className="job-text">
                        <strong>Oryginał:</strong> {job.text || 'Brak'}
                      </div>
                      {(job.status === 1 || job.status === 2) && job.processedText && (
                        <div className={`job-result ${job.status === 1 ? 'processing' : ''}`}>
                          <strong>{job.status === 1 ? 'Przetwarzanie...' : 'Przetworzone:'}</strong>{' '}
                          {job.type === 4 ? (
                            <span className="translated-text">
                              {formatTranslatedText(job.processedText)}
                              {job.status === 1 && job.processedText.endsWith('...') && (
                                <span className="typing-indicator">▋</span>
                              )}
                            </span>
                          ) : (
                            <>
                              {job.processedText}
                              {job.status === 1 && job.processedText.endsWith('...') && (
                                <span className="typing-indicator">▋</span>
                              )}
                            </>
                          )}
                        </div>
                      )}
                      {job.status === 3 && (
                        <div className="job-result error">
                          <strong>Błąd:</strong> Przetwarzanie zadania nie powiodło się
                        </div>
                      )}
                      <div className="job-time">
                        {job.createdAt ? new Date(job.createdAt).toLocaleString('pl-PL') : 'Brak'}
                        {job.processedAt && ` • Przetworzone: ${new Date(job.processedAt).toLocaleString('pl-PL')}`}
                      </div>
                      {job.status === 2 && (job.hasProcessedFile || job.processedText) && (
                        <div className="job-actions">
                          <button 
                            onClick={() => handleDownload(job.id)}
                            className="btn-download"
                          >
                            Pobierz wynik
                          </button>
                        </div>
                      )}
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
