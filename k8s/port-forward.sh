#!/bin/bash

# Script to start port forwarding for services
# Usage: ./port-forward.sh [start|stop|status]

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PID_FILE="$SCRIPT_DIR/.port-forward.pid"
LOG_FILE="$SCRIPT_DIR/.port-forward.log"

start_port_forward() {
    # Check if already running
    if [ -f "$PID_FILE" ]; then
        PID=$(cat "$PID_FILE")
        if ps -p "$PID" > /dev/null 2>&1; then
            echo "Port forwarding is already running (PID: $PID)"
            return 1
        else
            rm -f "$PID_FILE"
        fi
    fi

    echo "Starting port forwarding for services..."
    
    # Kill any existing port-forward processes
    pkill -f "kubectl port-forward" 2>/dev/null
    
    # Start port forwarding in background
    (
        kubectl port-forward svc/frontend 3000:80 >> "$LOG_FILE" 2>&1 &
        FRONTEND_PID=$!
        echo $FRONTEND_PID > "$PID_FILE"
        
        kubectl port-forward svc/producer-api 8080:80 >> "$LOG_FILE" 2>&1 &
        API_PID=$!
        echo "$API_PID" >> "$PID_FILE"
        
        kubectl port-forward svc/rabbitmq 15672:15672 >> "$LOG_FILE" 2>&1 &
        RABBITMQ_PID=$!
        echo "$RABBITMQ_PID" >> "$PID_FILE"
        
        # Wait for all processes
        wait $FRONTEND_PID $API_PID $RABBITMQ_PID
    ) &
    
    MAIN_PID=$!
    echo $MAIN_PID > "$PID_FILE"
    
    sleep 2
    
    if ps -p $MAIN_PID > /dev/null 2>&1; then
        echo "✅ Port forwarding started successfully!"
        echo ""
        echo "📊 Services available at:"
        echo "  Frontend:        http://localhost:3000"
        echo "  Producer API:    http://localhost:8080"
        echo "  RabbitMQ Mgmt:   http://localhost:15672 (guest/guest)"
        echo ""
        echo "To stop: ./port-forward.sh stop"
        echo "To view logs: tail -f $LOG_FILE"
    else
        echo "❌ Failed to start port forwarding. Check logs: $LOG_FILE"
        return 1
    fi
}

stop_port_forward() {
    if [ ! -f "$PID_FILE" ]; then
        echo "Port forwarding is not running"
        return 1
    fi
    
    echo "Stopping port forwarding..."
    
    # Kill all kubectl port-forward processes
    pkill -f "kubectl port-forward" 2>/dev/null
    
    # Remove PID file
    rm -f "$PID_FILE"
    
    echo "✅ Port forwarding stopped"
}

status_port_forward() {
    if [ -f "$PID_FILE" ]; then
        PID=$(cat "$PID_FILE" | head -1)
        if ps -p "$PID" > /dev/null 2>&1; then
            echo "✅ Port forwarding is running (PID: $PID)"
            echo ""
            echo "📊 Services available at:"
            echo "  Frontend:        http://localhost:3000"
            echo "  Producer API:    http://localhost:8080"
            echo "  RabbitMQ Mgmt:   http://localhost:15672"
            return 0
        else
            echo "❌ Port forwarding process not found (stale PID file)"
            rm -f "$PID_FILE"
            return 1
        fi
    else
        echo "❌ Port forwarding is not running"
        return 1
    fi
}

case "$1" in
    start)
        start_port_forward
        ;;
    stop)
        stop_port_forward
        ;;
    status)
        status_port_forward
        ;;
    *)
        echo "Usage: $0 {start|stop|status}"
        echo ""
        echo "Commands:"
        echo "  start   - Start port forwarding for all services"
        echo "  stop    - Stop port forwarding"
        echo "  status  - Check if port forwarding is running"
        exit 1
        ;;
esac

