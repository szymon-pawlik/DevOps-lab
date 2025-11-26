import { useState, useEffect, useRef } from 'react';
import axios from 'axios';
import * as signalR from '@microsoft/signalr';
import './App.scss';

interface Job {
  id: string;
  text: string;
  processedText?: string;
  status: number; // 0=Pending, 1=Processing, 2=Completed, 3=Failed
  createdAt: string;
  processedAt?: string;
}

// API URL - in browser always use localhost (ports are mapped in docker-compose)
const API_URL = import.meta.env.VITE_API_URL || 'http://localhost:8080';

function App() {
  const [text, setText] = useState('');
  const [jobs, setJobs] = useState<Job[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [apiStatus, setApiStatus] = useState<'checking' | 'online' | 'offline'>('checking');
  const [wsConnected, setWsConnected] = useState(false);
  const connectionRef = useRef<signalR.HubConnection | null>(null);

  const checkApiStatus = async () => {
    try {
      const response = await axios.get(`${API_URL}/api/job/health`);
      setApiStatus(response.status === 200 ? 'online' : 'offline');
    } catch {
      setApiStatus('offline');
    }
  };

  const fetchJobs = async () => {
    try {
      const response = await axios.get<Job[]>(`${API_URL}/api/job`);
      setJobs(response.data);
    } catch (err) {
      console.error('Failed to fetch jobs:', err);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!text.trim()) return;

    setLoading(true);
    setError(null);

    try {
      await axios.post(`${API_URL}/api/job`, {
        text: text.trim()
      });

      setText('');
      // Jobs will be updated via SignalR, but fetch once to be sure
      setTimeout(fetchJobs, 500);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to submit job');
    } finally {
      setLoading(false);
    }
  };

  // Setup SignalR connection
  useEffect(() => {
    const hubUrl = `${API_URL}/jobhub`;
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .build();

    connectionRef.current = connection;

    connection.on('JobCreated', (job: { id: string; text: string; status: number; createdAt: string }) => {
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
          status: job.status ?? 0,
          createdAt: job.createdAt || new Date().toISOString()
        };
        console.log('Adding new job:', newJob);
        return [newJob, ...prev];
      });
    });

    connection.on('JobUpdated', (update: { id: string; status: number; processedText?: string; processedAt?: string }) => {
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
        setWsConnected(true);
        console.log('SignalR connected');
      })
      .catch(err => {
        console.error('SignalR connection error:', err);
        setWsConnected(false);
      });

    connection.onreconnecting(() => {
      setWsConnected(false);
    });

    connection.onreconnected(() => {
      setWsConnected(true);
    });

    return () => {
      connection.stop();
    };
  }, []);

  // Check API status and fetch jobs on mount
  useEffect(() => {
    checkApiStatus();
    fetchJobs();
    
    const statusInterval = setInterval(checkApiStatus, 30000); // Check every 30s
    
    return () => {
      clearInterval(statusInterval);
    };
  }, []);

  return (
    <div className="app">
      <header className="header">
        <h1>Producer-Consumer System</h1>
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
              <label htmlFor="job-text">Text to Process:</label>
              <textarea
                id="job-text"
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder="Enter text to process (will be converted to uppercase)..."
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
                          <strong>Processed:</strong> {job.processedText}
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
